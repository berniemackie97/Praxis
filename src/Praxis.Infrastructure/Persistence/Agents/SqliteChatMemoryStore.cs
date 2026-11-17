using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Praxis.Application.Agents.Memory;

namespace Praxis.Infrastructure.Persistence.Agents;

/// <summary>Durable cursor over completed chat dialogue. No transcript copy and no remote disclosure path.</summary>
public sealed class SqliteChatMemoryStore : IChatMemoryStore
{
    private readonly CatalogDatabase database;
    private readonly TimeProvider clock;

    internal SqliteChatMemoryStore(CatalogDatabase database, TimeProvider clock) =>
        (this.database, this.clock) = (database, clock);

    internal static void Schedule(SqliteConnection connection, SqliteTransaction transaction, Guid conversationId, DateTimeOffset now) =>
        connection.Run(transaction, """
        INSERT INTO agent_chat_recaps(conversation_id, pending_through, retry_utc)
        SELECT $id, MAX(sequence), $due FROM agent_messages WHERE conversation_id = $id HAVING count(*) > 0
        ON CONFLICT(conversation_id) DO UPDATE SET pending_through = excluded.pending_through, retry_utc = excluded.retry_utc
        WHERE excluded.pending_through > agent_chat_recaps.pending_through
        """, ("$id", conversationId.ToString("D")), ("$due", At(now.AddSeconds(8))));

    public ChatMemoryWork? Next() => database.ExecuteRead(connection =>
    {
        using var job = connection.CreateCommand("""
            SELECT conversation_id, pending_through, reviewed_through, recap FROM agent_chat_recaps
            WHERE retry_utc <= $now AND reviewed_through < pending_through ORDER BY retry_utc, conversation_id LIMIT 1
            """);
        job.Parameters.AddWithValue("$now", At(clock.GetUtcNow()));
        using var reader = job.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        Guid id = Guid.Parse(reader.GetString(0));
        int pending = reader.GetInt32(1), reviewed = reader.GetInt32(2);
        string? recap = reader.IsDBNull(3) ? null : reader.GetString(3);
        reader.Close();

        using var messages = connection.CreateCommand("""
            SELECT sequence, role, substr(content, 1, CASE role WHEN 'user' THEN 1400 ELSE 400 END) FROM agent_messages
            WHERE conversation_id = $id AND sequence > $reviewed AND sequence <= $pending
              AND ((role = 'user' AND content NOT LIKE 'Answer check failed.%') OR (role = 'assistant' AND tool_calls = '[]'))
            ORDER BY sequence LIMIT 8
            """);
        messages.Parameters.AddWithValue("$id", id.ToString("D"));
        messages.Parameters.AddWithValue("$reviewed", reviewed);
        messages.Parameters.AddWithValue("$pending", pending);
        using var dialogue = messages.ExecuteReader();
        List<(bool, string)> lines = [];
        int last = reviewed;
        while (dialogue.Read())
        {
            last = dialogue.GetInt32(0);
            lines.Add((dialogue.GetString(1) == "user", dialogue.GetString(2)));
        }
        if (lines.Count == 8)
        {
            pending = last;
        }

        return new ChatMemoryWork(id, pending, recap, lines);
    });

    public bool IsPending(ChatMemoryWork work) => database.ExecuteRead(connection =>
    {
        using var query = connection.CreateCommand("""
            SELECT 1 FROM agent_chat_recaps WHERE conversation_id = $id AND reviewed_through < $through AND pending_through >= $through
            """);
        query.Parameters.AddWithValue("$id", work.ConversationId.ToString("D"));
        query.Parameters.AddWithValue("$through", work.Through);
        return query.ExecuteScalar() is long;
    });

    public bool SaveRecap(ChatMemoryWork work, string recap) => Write("""
        UPDATE agent_chat_recaps SET reviewed_through = $through, recap = $recap
        WHERE conversation_id = $id AND reviewed_through < $through AND pending_through >= $through
        """, ("$id", work.ConversationId.ToString("D")), ("$through", work.Through), ("$recap", recap)) == 1;

    public void Retry(ChatMemoryWork work) => Write("""
        UPDATE agent_chat_recaps SET retry_utc = $due WHERE conversation_id = $id AND reviewed_through < $through
        """, ("$id", work.ConversationId.ToString("D")), ("$due", At(clock.GetUtcNow().AddMinutes(2))), ("$through", work.Through));

    public string? ReadRecap(Guid conversationId) => Scalar("SELECT recap FROM agent_chat_recaps WHERE conversation_id = $id", ("$id", conversationId.ToString("D")));

    public IReadOnlyList<ChatMemoryMatch> Search(string query, Guid? excludeConversationId, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 5);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.Length, 256);
        string[] terms = Regex.Matches(ChatMemoryQuery.Build(query), @"[\p{L}\p{N}_]{3,48}", RegexOptions.None, TimeSpan.FromMilliseconds(50))
            .Select(match => match.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToArray();
        if (terms.Length == 0)
        {
            return [];
        }

        return database.ExecuteRead(connection =>
        {
            using var command = connection.CreateCommand($$"""
                SELECT c.conversation_id, c.title, r.recap, r.reviewed_through
                FROM agent_recap_search f JOIN agent_chat_recaps r ON r.conversation_id = f.conversation_id
                JOIN agent_conversations c ON c.conversation_id = r.conversation_id
                WHERE agent_recap_search MATCH $query AND ($exclude IS NULL OR c.conversation_id <> $exclude)
                  AND NOT EXISTS (
                    SELECT 1 FROM agent_messages m, json_each(m.citations) citation
                    WHERE m.conversation_id = c.conversation_id AND m.sequence <= r.reviewed_through
                      AND {{AgentEvidenceSql.ChangedCitation}})
                ORDER BY bm25(agent_recap_search), c.updated_utc DESC, c.conversation_id LIMIT $limit
                """);
            command.Parameters.AddWithValue("$query", string.Join(" AND ", terms.Select(term => '"' + term + '"')));
            command.Parameters.AddWithValue("$exclude", (object?)excludeConversationId?.ToString("D") ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            List<ChatMemoryMatch> matches = [];
            while (reader.Read())
            {
                matches.Add(new ChatMemoryMatch(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2), reader.GetInt32(3)));
            }
            return matches;
        });
    }
    public string? ReadName() => Scalar("SELECT display_name FROM agent_identity WHERE singleton = 1");

    public void SaveName(string name) => Write("""
        INSERT INTO agent_identity (singleton, display_name) VALUES (1, $name)
        ON CONFLICT(singleton) DO UPDATE SET display_name = excluded.display_name
        """, ("$name", name));

    private int Write(string sql, params (string Name, object Value)[] arguments) => database.Execute(connection =>
    {
        using var command = connection.CreateCommand(sql);
        foreach (var (name, value) in arguments)
        {
            command.Parameters.AddWithValue(name, value);
        }
        return command.ExecuteNonQuery();
    });

    private string? Scalar(string sql, (string Name, object Value)? argument = null) => database.ExecuteRead(connection =>
    {
        using var query = connection.CreateCommand(sql);
        if (argument is { } parameter)
        {
            query.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        return query.ExecuteScalar() as string;
    });

    private static string At(DateTimeOffset time) => CatalogValues.Timestamp(time);
}
