using Microsoft.Data.Sqlite;

namespace Praxis.Infrastructure.Persistence.Knowledge;

/// <summary>Marks entities for a second look. An entity already waiting keeps its earlier reason.</summary>
internal static class ReconsolidationQueue
{
    internal static void Enqueue(SqliteConnection connection, SqliteTransaction transaction,
        IEnumerable<string> entities, string reason, string now)
    {
        foreach (string entity in entities.Distinct(StringComparer.Ordinal))
        {
            connection.Run(transaction, """
                INSERT INTO reconsolidation_queue (entity_id, reason, previous_type, enqueued_utc)
                SELECT $entity, $reason, NULL, $now
                WHERE EXISTS (SELECT 1 FROM knowledge_entities WHERE entity_id = $entity AND status = 'active')
                ON CONFLICT(entity_id) DO NOTHING
                """, ("$entity", entity), ("$reason", reason), ("$now", now));
        }
    }
}
