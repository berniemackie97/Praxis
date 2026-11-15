using Microsoft.Data.Sqlite;
using Praxis.Application.Agents.Skills;

namespace Praxis.Infrastructure.Persistence.Agents;

/// <summary>Immutable owner skill revisions and their currently approved pins.</summary>
public sealed class SqliteSkillRepository : ISkillRepository
{
    private const int MaximumInstalled = 200;
    private const int MaximumPending = 100;
    private const string SummaryColumns = "revision_id, name, description, digest, base_revision_id, conversation_id, origin, rationale, state, created_utc";

    private readonly CatalogDatabase database;
    private readonly TimeProvider clock;

    internal SqliteSkillRepository(CatalogDatabase database, TimeProvider clock)
    {
        this.database = database;
        this.clock = clock;
    }

    public IReadOnlyList<SkillDescriptor> Installed() => database.ExecuteRead<IReadOnlyList<SkillDescriptor>>(connection =>
    {
        using SqliteCommand command = connection.CreateCommand("""
            SELECT i.name, r.description, r.digest, i.revision_id
            FROM agent_skill_installs i JOIN agent_skill_revisions r ON r.revision_id = i.revision_id
            WHERE r.state = 'accepted' ORDER BY i.name
            """);
        using SqliteDataReader reader = command.ExecuteReader();
        List<SkillDescriptor> skills = [];
        while (reader.Read())
        {
            skills.Add(new SkillDescriptor(reader.GetString(0), reader.GetString(1), reader.GetString(2), Guid.Parse(reader.GetString(3)), false, true));
        }

        return skills;
    });

    public IReadOnlySet<string> DisabledNames() => database.ExecuteRead<IReadOnlySet<string>>(connection =>
    {
        using SqliteCommand command = connection.CreateCommand("SELECT name FROM agent_skill_disabled");
        using SqliteDataReader reader = command.ExecuteReader();
        HashSet<string> disabled = new(StringComparer.Ordinal);
        while (reader.Read())
        {
            disabled.Add(reader.GetString(0));
        }

        return disabled;
    });

    public SkillRevision? Read(Guid id) => database.ExecuteRead(connection => Read(connection, null, id));

    public SkillRevision? ReadInstalled(string name, bool enabledOnly) => database.ExecuteRead(connection =>
    {
        using SqliteCommand command = connection.CreateCommand($"""
            SELECT {SummaryColumns}, markdown FROM agent_skill_revisions
            WHERE revision_id = (SELECT revision_id FROM agent_skill_installs WHERE name = $name)
              AND state = 'accepted'
              AND ($enabledOnly = 0 OR NOT EXISTS (SELECT 1 FROM agent_skill_disabled WHERE name = $name))
            """);
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$enabledOnly", enabledOnly ? 1 : 0);
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? new SkillRevision(Summary(reader), reader.GetString(10)) : null;
    });

    public IReadOnlyList<SkillRevisionSummary> History(string? name, bool pendingOnly, int limit) =>
        database.ExecuteRead<IReadOnlyList<SkillRevisionSummary>>(connection =>
        {
            using SqliteCommand command = connection.CreateCommand($"""
                SELECT {SummaryColumns} FROM agent_skill_revisions
                WHERE ($name IS NULL OR name = $name) AND ($pending = 0 OR state = 'pending')
                ORDER BY created_utc DESC, rowid DESC LIMIT $limit
                """);
            command.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
            command.Parameters.AddWithValue("$pending", pendingOnly ? 1 : 0);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
            using SqliteDataReader reader = command.ExecuteReader();
            List<SkillRevisionSummary> revisions = [];
            while (reader.Read())
            {
                revisions.Add(Summary(reader));
            }

            return revisions;
        });

    public SkillRevisionSummary Propose(SkillDocument document, Guid? conversationId, string rationale, Guid? expectedBase) =>
        database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            Guid? baseline = Pinned(connection, transaction, document.Name);
            if (conversationId.HasValue && baseline != expectedBase)
            {
                throw new InvalidOperationException("Open the current skill and use its revisionId before proposing an update.");
            }

            if (baseline is { } id && Read(connection, transaction, id)?.Summary.Digest == document.Digest)
            {
                throw new ArgumentException("This is already the installed version.");
            }

            using (SqliteCommand pending = connection.CreateCommand($"""
                SELECT {SummaryColumns} FROM agent_skill_revisions
                WHERE state = 'pending' AND name = $name AND digest = $digest AND base_revision_id IS $base LIMIT 1
                """))
            {
                pending.Transaction = transaction;
                pending.Parameters.AddWithValue("$name", document.Name);
                pending.Parameters.AddWithValue("$digest", document.Digest);
                pending.Parameters.AddWithValue("$base", (object?)baseline?.ToString("D") ?? DBNull.Value);
                using SqliteDataReader reader = pending.ExecuteReader();
                if (reader.Read())
                {
                    return Summary(reader);
                }
            }

            if (Count(connection, transaction, "SELECT COUNT(*) FROM agent_skill_revisions WHERE state = 'pending'") >= MaximumPending)
            {
                throw new InvalidOperationException("Review or reject pending skill drafts before adding more.");
            }

            SkillRevisionSummary summary = new(Guid.NewGuid(), document.Name, document.Description, document.Digest, baseline, conversationId,
                conversationId.HasValue ? "chat" : "import", rationale, "pending", clock.GetUtcNow());
            connection.Run(transaction, """
                INSERT INTO agent_skill_revisions
                (revision_id, name, description, markdown, digest, base_revision_id, conversation_id, origin, rationale, state, created_utc)
                VALUES ($id, $name, $description, $markdown, $digest, $base, $conversation, $origin, $rationale, 'pending', $now)
                """, ("$id", summary.RevisionId.ToString("D")), ("$name", document.Name), ("$description", document.Description),
                ("$markdown", document.Markdown), ("$digest", document.Digest), ("$base", baseline?.ToString("D")),
                ("$conversation", conversationId?.ToString("D")), ("$origin", summary.Origin), ("$rationale", rationale),
                ("$now", CatalogValues.Timestamp(summary.CreatedUtc)));
            transaction.Commit();
            return summary;
        });

    public bool Resolve(Guid id, bool accept) => database.Execute(connection =>
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        if (Read(connection, transaction, id)?.Summary is not { State: "pending" } draft)
        {
            return false;
        }

        if (accept)
        {
            Guid? current = Pinned(connection, transaction, draft.Name);
            if (current != draft.BaseRevisionId)
            {
                throw new InvalidOperationException("The installed version changed since this draft. Reject it and propose a new draft against the current version.");
            }

            if (current is null && Count(connection, transaction, "SELECT COUNT(*) FROM agent_skill_installs") >= MaximumInstalled)
            {
                throw new InvalidOperationException("The installed skill limit has been reached.");
            }

            connection.Run(transaction, """
                INSERT INTO agent_skill_installs (name, revision_id) VALUES ($name, $id)
                ON CONFLICT(name) DO UPDATE SET revision_id = excluded.revision_id
                """, ("$name", draft.Name), ("$id", id.ToString("D")));
        }

        connection.Run(transaction, """
            UPDATE agent_skill_revisions SET state = $state, decided_utc = $now WHERE revision_id = $id
            """, ("$state", accept ? "accepted" : "rejected"), ("$now", CatalogValues.Timestamp(clock.GetUtcNow())), ("$id", id.ToString("D")));
        transaction.Commit();
        return true;
    });

    public bool SetEnabled(string name, bool enabled) => database.Execute(connection =>
    {
        using SqliteCommand command = connection.CreateCommand(enabled ? "DELETE FROM agent_skill_disabled WHERE name = $name"
            : "INSERT OR IGNORE INTO agent_skill_disabled (name) VALUES ($name)");
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteNonQuery() == 1;
    });

    private static Guid? Pinned(SqliteConnection connection, SqliteTransaction transaction, string name)
    {
        using SqliteCommand command = connection.CreateCommand("SELECT revision_id FROM agent_skill_installs WHERE name = $name");
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$name", name);
        return command.ExecuteScalar() is string value ? Guid.Parse(value) : null;
    }

    private static int Count(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using SqliteCommand command = connection.CreateCommand(sql);
        command.Transaction = transaction;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static SkillRevision? Read(SqliteConnection connection, SqliteTransaction? transaction, Guid id)
    {
        using SqliteCommand command = connection.CreateCommand($"SELECT {SummaryColumns}, markdown FROM agent_skill_revisions WHERE revision_id = $id");
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read() ? new SkillRevision(Summary(reader), reader.GetString(10)) : null;
    }

    private static SkillRevisionSummary Summary(SqliteDataReader reader) => new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
        reader.GetString(3), ReadId(reader, 4), ReadId(reader, 5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
        CatalogValues.ParseTimestamp(reader.GetString(9)));

    private static Guid? ReadId(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : Guid.Parse(reader.GetString(column));
}
