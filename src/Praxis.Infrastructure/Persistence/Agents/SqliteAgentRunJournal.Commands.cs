using System.Globalization;
using System.Text.Json;
using Praxis.Domain.Agents;
using Praxis.Domain.Knowledge;

namespace Praxis.Infrastructure.Persistence.Agents;

public sealed partial class SqliteAgentRunJournal
{
    public bool RecentlyReviewed(KnowledgeSnapshot snapshot) => database.ExecuteRead(connection =>
    {
        using var query = connection.CreateCommand("""
            SELECT EXISTS(SELECT 1 FROM agent_run_reviews reviewed JOIN agent_runs r ON r.run_id=reviewed.run_id
            WHERE r.state='completed' AND r.kind='dream' AND r.completed_utc >= $since
              AND reviewed.entity_id=$entity AND reviewed.fingerprint=$fingerprint)
            """);
        query.Parameters.AddWithValue("$since", CatalogValues.Timestamp(clock.GetUtcNow().AddDays(-1)));
        query.Parameters.AddWithValue("$entity", snapshot.EntityId.ToString("D"));
        query.Parameters.AddWithValue("$fingerprint", snapshot.Fingerprint);
        return Convert.ToInt32(query.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
    });

    public IReadOnlyList<AgentCommandHistory> CommandHistory(Guid conversationId, int limit) => database.ExecuteRead(connection =>
    {
        using var query = connection.CreateCommand("""
            SELECT message_end,json_extract(reply,'$.command') FROM agent_runs
            WHERE conversation_id=$conversation AND state='completed' AND json_type(reply,'$.command')='object'
            ORDER BY completed_utc DESC LIMIT $limit
            """);
        query.Parameters.AddWithValue("$conversation", conversationId.ToString("D"));
        query.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 100));
        using var rows = query.ExecuteReader();
        List<AgentCommandHistory> results = [];
        while (rows.Read())
        {
            results.Add(new(rows.GetInt32(0), JsonSerializer.Deserialize<AgentCommandReceipt>(rows.GetString(1), Json)!));
        }
        return results;
    });
}
