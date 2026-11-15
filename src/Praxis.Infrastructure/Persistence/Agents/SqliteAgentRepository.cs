using System.Text.Json;
using Microsoft.Data.Sqlite;
using Praxis.Application.Agents;
using Praxis.Domain.Agents;
using Praxis.Domain.Models;

namespace Praxis.Infrastructure.Persistence.Agents;

/// <summary>Owner-private agent state: backends, conversations, messages, tool-call audit, proposals, memories and artifacts.</summary>
public sealed class SqliteAgentRepository : IAgentConversationRepository
{
    private const int MaximumMessages = 2_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CatalogDatabase database;
    private readonly TimeProvider clock;

    internal SqliteAgentRepository(CatalogDatabase database, TimeProvider clock)
    {
        this.database = database;
        this.clock = clock;
    }

    public IReadOnlyList<AgentBackendProfile> ReadBackends() => database.ExecuteRead(connection =>
    {
        using SqliteCommand command = connection.CreateCommand(
            "SELECT backend_id, kind, endpoint, model, key_account, executable, enabled, effort FROM agent_backends ORDER BY backend_id");
        using SqliteDataReader reader = command.ExecuteReader();
        List<AgentBackendProfile> profiles = [];
        while (reader.Read())
        {
            profiles.Add(new AgentBackendProfile(reader.GetString(0), reader.GetString(1), Text(reader, 2), reader.GetString(3), Text(reader, 4),
                Text(reader, 5), reader.GetInt64(6) == 1, Text(reader, 7)));
        }

        return profiles;
    });

    public void SaveBackend(AgentBackendProfile profile) => database.Execute(_ =>
    {
        profile.Validate();
        AgentBackendProfile? existing = ReadBackends().FirstOrDefault(item => item.BackendId == profile.BackendId);
        if (existing is not null && existing.IsLocal != profile.IsLocal)
        {
            throw new ArgumentException("Use a new backend ID when changing local or remote access. Existing conversations keep their privacy boundary.");
        }

        Write("""
            INSERT INTO agent_backends (backend_id, kind, endpoint, model, key_account, executable, enabled, updated_utc, effort)
            VALUES ($id, $kind, $endpoint, $model, $key, $executable, $enabled, $now, $effort)
            ON CONFLICT(backend_id) DO UPDATE SET kind = excluded.kind, endpoint = excluded.endpoint, model = excluded.model,
                key_account = excluded.key_account, executable = excluded.executable, enabled = excluded.enabled, updated_utc = excluded.updated_utc,
                effort = excluded.effort
            """, ("$effort", profile.Effort), ("$id", profile.BackendId), ("$kind", profile.Kind), ("$endpoint", profile.Endpoint), ("$model", profile.Model),
            ("$key", profile.KeyAccount), ("$executable", profile.Executable), ("$enabled", profile.Enabled ? 1 : 0), ("$now", Now()));
    });

    public AgentConversation Create(string backendId, string title, string? model = null, string? effort = null)
    {
        AgentBackendProfile backend = ReadBackends().FirstOrDefault(profile => profile.BackendId == backendId && profile.Enabled)
            ?? throw new ArgumentException("Connect and enable this chat model before starting a conversation.", nameof(backendId));
        ValidateModelChoice(backend, model, effort);

        string bounded = string.IsNullOrWhiteSpace(title) ? "New conversation" : title.Trim().Length > 200 ? title.Trim()[..200] : title.Trim();
        DateTimeOffset now = clock.GetUtcNow();
        AgentConversation conversation = new(Guid.NewGuid(), backendId, bounded, now, now,
            model ?? backend.Model, model is null ? backend.Effort : effort);
        Write("""
            INSERT INTO agent_conversations (conversation_id, backend_id, title, created_utc, updated_utc, model, effort)
            VALUES ($id, $backend, $title, $now, $now, $model, $effort)
            """, ("$id", conversation.ConversationId.ToString("D")), ("$backend", backendId), ("$title", bounded),
            ("$now", CatalogValues.Timestamp(now)), ("$model", conversation.Model), ("$effort", conversation.Effort));
        return conversation;
    }

    public AgentConversation? Read(Guid conversationId) => Conversations("WHERE conversation_id = $id", 1, ("$id", conversationId.ToString("D"))).FirstOrDefault();

    public AgentConversation SetModel(Guid conversationId, string model, string? effort)
    {
        AgentConversation conversation = Read(conversationId) ?? throw new ArgumentException("Unknown conversation.");
        AgentBackendProfile backend = ReadBackends().FirstOrDefault(profile => profile.BackendId == conversation.BackendId && profile.Enabled)
            ?? throw new ArgumentException("Enable this conversation's connection in Models first.");
        ValidateModelChoice(backend, model, effort);
        if (Write("UPDATE agent_conversations SET model = $model, effort = $effort WHERE conversation_id = $id",
                ("$model", model), ("$effort", effort), ("$id", conversationId.ToString("D"))) != 1)
        {
            throw new ArgumentException("Unknown conversation.");
        }

        return conversation with { Model = model, Effort = effort };
    }

    private static void ValidateModelChoice(AgentBackendProfile backend, string? model, string? effort)
    {
        if (model is not null && (model.Length is < 1 or > 200 || model.Any(char.IsWhiteSpace) || model.Any(char.IsControl)) ||
            !ModelEffort.IsValid(effort) || effort is not null && !AgentBackendKinds.IsSession(backend.Kind))
        {
            throw new ArgumentException("The conversation model or effort is invalid.");
        }
    }

    public bool Rename(Guid conversationId, string title)
    {
        string normalized = title.Trim();
        if (normalized.Length is < 1 or > 200)
        {
            throw new ArgumentException("The conversation title must have 1–200 characters.", nameof(title));
        }

        return Write("UPDATE agent_conversations SET title = $title, updated_utc = $now WHERE conversation_id = $id",
            ("$title", normalized), ("$now", Now()), ("$id", conversationId.ToString("D"))) == 1;
    }

    public bool Delete(Guid conversationId) => database.Execute(connection =>
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        string id = conversationId.ToString("D");
        // Independently owned artifacts, memories and pending owner proposals survive without a chat link.
        connection.Run(transaction, "UPDATE agent_artifacts SET conversation_id = NULL WHERE conversation_id = $id", ("$id", id));
        connection.Run(transaction, "UPDATE agent_memories SET conversation_id = NULL WHERE conversation_id = $id", ("$id", id));
        connection.Run(transaction, "UPDATE agent_proposals SET conversation_id = NULL WHERE conversation_id = $id", ("$id", id));
        // Remove transcript and tool-call argument history explicitly; do not rely on SQLite FK defaults.
        connection.Run(transaction, "DELETE FROM agent_tool_calls WHERE conversation_id = $id", ("$id", id));
        connection.Run(transaction, "DELETE FROM agent_messages WHERE conversation_id = $id", ("$id", id));
        int deleted = connection.Run(transaction, "DELETE FROM agent_conversations WHERE conversation_id = $id", ("$id", id));
        transaction.Commit();
        return deleted == 1;
    });

    public IReadOnlyList<AgentConversation> List(int limit) => Conversations("", Math.Clamp(limit, 1, 500));

    public IReadOnlyList<AgentMessage> ReadMessages(Guid conversationId) => database.ExecuteRead(connection =>
    {
        using SqliteCommand command = connection.CreateCommand("""
            SELECT role, content, tool_calls, tool_call_id FROM agent_messages WHERE conversation_id = $id ORDER BY sequence DESC LIMIT $limit
            """);
        command.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        command.Parameters.AddWithValue("$limit", MaximumMessages);
        using SqliteDataReader reader = command.ExecuteReader();
        List<AgentMessage> messages = [];
        while (reader.Read())
        {
            messages.Add(new AgentMessage(reader.GetString(0), reader.GetString(1),
                JsonSerializer.Deserialize<AgentToolCall[]>(reader.GetString(2), Json) ?? [], Text(reader, 3), []));
        }

        messages.Reverse();
        return messages;
    });

    public void Append(Guid conversationId, AgentMessage message)
    {
        if (message.Content.Length > 400_000)
        {
            throw new ArgumentException("The message is too large.", nameof(message));
        }

        database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            string now = Now();
            int inserted = connection.Run(transaction, """
                INSERT INTO agent_messages (conversation_id, sequence, role, content, tool_calls, tool_call_id, created_utc)
                SELECT $id, COALESCE(MAX(sequence), 0) + 1, $role, $content, $calls, $callId, $now FROM agent_messages WHERE conversation_id = $id
                """, ("$id", conversationId.ToString("D")), ("$role", message.Role), ("$content", message.Content),
                ("$calls", JsonSerializer.Serialize(message.ToolCalls, Json)), ("$callId", message.ToolCallId), ("$now", now));
            if (inserted != 1 || connection.Run(transaction, "UPDATE agent_conversations SET updated_utc = $now WHERE conversation_id = $id",
                    ("$now", now), ("$id", conversationId.ToString("D"))) != 1)
            {
                throw new ArgumentException("Unknown conversation.", nameof(conversationId));
            }

            transaction.Commit();
        });
    }

    public void RecordToolCall(AgentToolCallRecord record) => Write("""
        INSERT INTO agent_tool_calls (conversation_id, call_id, tool, tier, arguments, outcome, result_characters, citations, withheld, started_utc, duration_ms)
        VALUES ($conversation, $call, $tool, $tier, $arguments, $outcome, $characters, $citations, $withheld, $started, $duration)
        """, ("$conversation", record.ConversationId.ToString("D")), ("$call", Bound(record.CallId, 128)), ("$tool", Bound(record.Tool, 64)),
        ("$tier", Bound(record.Tier, 16)), ("$arguments", Bound(record.ArgumentsJson, 4_000)), ("$outcome", Bound(record.Outcome, 32)),
        ("$characters", record.ResultCharacters), ("$citations", record.Citations), ("$withheld", record.Withheld),
        ("$started", CatalogValues.Timestamp(record.StartedUtc)), ("$duration", (long)record.Duration.TotalMilliseconds));

    public IReadOnlyList<AgentToolCallRecord> ReadToolCalls(Guid conversationId, int limit) => database.ExecuteRead(connection =>
    {
        using SqliteCommand command = connection.CreateCommand("""
            SELECT call_id, tool, tier, arguments, outcome, result_characters, citations, withheld, started_utc, duration_ms
            FROM agent_tool_calls WHERE conversation_id = $id ORDER BY record_id DESC LIMIT $limit
            """);
        command.Parameters.AddWithValue("$id", conversationId.ToString("D"));
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1_000));
        using SqliteDataReader reader = command.ExecuteReader();
        List<AgentToolCallRecord> records = [];
        while (reader.Read())
        {
            records.Add(new AgentToolCallRecord(conversationId, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7), CatalogValues.ParseTimestamp(reader.GetString(8)),
                TimeSpan.FromMilliseconds(reader.GetInt64(9))));
        }

        return records;
    });

    public AgentProposal AddProposal(Guid? conversationId, string kind, string payloadJson, string rationale, string status)
    {
        AgentProposal proposal = new(Guid.NewGuid(), conversationId, kind, payloadJson, rationale, status, clock.GetUtcNow());
        Write("""
            INSERT INTO agent_proposals (proposal_id, conversation_id, kind, payload, rationale, status, created_utc, resolved_utc)
            VALUES ($id, $conversation, $kind, $payload, $rationale, $status, $now, CASE WHEN $status = 'pending' THEN NULL ELSE $now END)
            """, ("$id", proposal.ProposalId.ToString("D")), ("$conversation", conversationId?.ToString("D")), ("$kind", kind), ("$payload", payloadJson),
            ("$rationale", rationale), ("$status", status), ("$now", CatalogValues.Timestamp(proposal.CreatedUtc)));
        return proposal;
    }

    public IReadOnlyList<AgentProposal> ReadProposals(string? status, int limit) =>
        Proposals(status is null ? "" : "WHERE status = $status", Math.Clamp(limit, 1, 500), ("$status", status));

    public AgentProposal? ReadProposal(Guid proposalId) => Proposals("WHERE proposal_id = $id", 1, ("$id", proposalId.ToString("D"))).FirstOrDefault();

    public bool SetProposalStatus(Guid proposalId, string expectedStatus, string status) => Write("""
        UPDATE agent_proposals SET status = $status, resolved_utc = $now WHERE proposal_id = $id AND status = $expected
        """, ("$status", status), ("$now", Now()), ("$id", proposalId.ToString("D")), ("$expected", expectedStatus)) == 1;

    public AgentMemory AddMemory(Guid? conversationId, string text, string status)
    {
        AgentMemory memory = new(Guid.NewGuid(), text.Trim(), status, conversationId, clock.GetUtcNow());
        Write("""
            INSERT INTO agent_memories (memory_id, conversation_id, text, status, created_utc, updated_utc) VALUES ($id, $conversation, $text, $status, $now, $now)
            """, ("$id", memory.MemoryId.ToString("D")), ("$conversation", conversationId?.ToString("D")), ("$text", memory.Text), ("$status", status),
            ("$now", CatalogValues.Timestamp(memory.CreatedUtc)));
        return memory;
    }

    public IReadOnlyList<AgentMemory> ReadMemories(string? status, string? search, int limit) => database.ExecuteRead(connection =>
    {
        using SqliteCommand command = connection.CreateCommand("""
            SELECT memory_id, text, status, conversation_id, created_utc FROM agent_memories
            WHERE ($status IS NULL OR status = $status) AND ($search IS NULL OR instr(lower(text), lower($search)) > 0)
            ORDER BY created_utc DESC LIMIT $limit
            """);
        command.Parameters.AddWithValue("$status", (object?)status ?? DBNull.Value);
        command.Parameters.AddWithValue("$search", (object?)search ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        using SqliteDataReader reader = command.ExecuteReader();
        List<AgentMemory> memories = [];
        while (reader.Read())
        {
            memories.Add(new AgentMemory(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)), CatalogValues.ParseTimestamp(reader.GetString(4))));
        }

        return memories;
    });

    public bool SetMemoryStatus(Guid memoryId, string status) => Write("""
        UPDATE agent_memories SET status = $status, updated_utc = $now WHERE memory_id = $id
        """, ("$status", status), ("$now", Now()), ("$id", memoryId.ToString("D"))) == 1;

    public void SaveArtifact(AgentArtifact artifact) => Write("""
        INSERT INTO agent_artifacts (artifact_id, title, source_id, revision, path, conversation_id, updated_utc)
        VALUES ($id, $title, $source, $revision, $path, $conversation, $now)
        ON CONFLICT(artifact_id) DO UPDATE SET title = excluded.title, source_id = excluded.source_id, revision = excluded.revision,
            path = excluded.path, updated_utc = excluded.updated_utc
        """, ("$id", artifact.ArtifactId.ToString("D")), ("$title", artifact.Title), ("$source", artifact.SourceId.ToString("D")),
        ("$revision", artifact.Revision), ("$path", artifact.Path), ("$conversation", artifact.ConversationId?.ToString("D")),
        ("$now", CatalogValues.Timestamp(artifact.UpdatedUtc)));

    public AgentArtifact? ReadArtifact(Guid artifactId) => Artifacts("WHERE artifact_id = $id", 1, ("$id", artifactId.ToString("D"))).FirstOrDefault();

    public IReadOnlyList<AgentArtifact> ReadArtifacts(int limit) => Artifacts("", Math.Clamp(limit, 1, 500));

    private IReadOnlyList<AgentConversation> Conversations(string where, int limit, params (string, object?)[] parameters) => Query(
        $"SELECT conversation_id, backend_id, title, created_utc, updated_utc, model, effort FROM agent_conversations {where} ORDER BY updated_utc DESC LIMIT {limit}",
        reader => new AgentConversation(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
            CatalogValues.ParseTimestamp(reader.GetString(3)), CatalogValues.ParseTimestamp(reader.GetString(4)),
            Text(reader, 5), Text(reader, 6)), parameters);

    private IReadOnlyList<AgentProposal> Proposals(string where, int limit, params (string, object?)[] parameters) => Query(
        $"SELECT proposal_id, conversation_id, kind, payload, rationale, status, created_utc FROM agent_proposals {where} ORDER BY created_utc DESC LIMIT {limit}",
        reader => new AgentProposal(Guid.Parse(reader.GetString(0)), reader.IsDBNull(1) ? null : Guid.Parse(reader.GetString(1)), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), reader.GetString(5), CatalogValues.ParseTimestamp(reader.GetString(6))), parameters);

    private IReadOnlyList<AgentArtifact> Artifacts(string where, int limit, params (string, object?)[] parameters) => Query(
        $"SELECT artifact_id, title, source_id, revision, path, conversation_id, updated_utc FROM agent_artifacts {where} ORDER BY updated_utc DESC LIMIT {limit}",
        reader => new AgentArtifact(Guid.Parse(reader.GetString(0)), reader.GetString(1), Guid.Parse(reader.GetString(2)), reader.GetInt32(3),
            reader.GetString(4), reader.IsDBNull(5) ? null : Guid.Parse(reader.GetString(5)), CatalogValues.ParseTimestamp(reader.GetString(6))), parameters);

    private IReadOnlyList<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters) => database.ExecuteRead(connection =>
    {
        using SqliteCommand command = connection.CreateCommand(sql);
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        using SqliteDataReader reader = command.ExecuteReader();
        List<T> rows = [];
        while (reader.Read())
        {
            rows.Add(map(reader));
        }

        return rows;
    });

    private int Write(string sql, params (string Name, object? Value)[] parameters) => database.Execute(connection =>
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        int changed = connection.Run(transaction, sql, parameters);
        transaction.Commit();
        return changed;
    });

    private static string? Text(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);

    private static string Bound(string value, int limit) => value.Length <= limit ? value : value[..limit];

    private string Now() => CatalogValues.Timestamp(clock.GetUtcNow());
}
