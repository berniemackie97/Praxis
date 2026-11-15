using Microsoft.Data.Sqlite;
using Praxis.Domain.Knowledge;

namespace Praxis.Infrastructure.Persistence.Knowledge;

/// <summary>Writes revisions. A proposal is never repeated while open or after the owner dismissed it.</summary>
internal static class KnowledgeRevisionLog
{
    internal static void Applied(SqliteConnection connection, SqliteTransaction transaction, string entity,
        string? before, string after, string trigger, string basis, string now) =>
        connection.Run(transaction, """
            INSERT INTO knowledge_revisions (entity_id, field, before_value, after_value, trigger, basis, state, created_utc)
            VALUES ($entity, 'entity_type', $before, $after, $trigger, $basis, 'applied', $now)
            """, ("$entity", entity), ("$before", before), ("$after", after), ("$trigger", trigger),
            ("$basis", Clip(basis)), ("$now", now));

    /// <summary>Records a proposal; returns false when the same proposal is open or was dismissed.</summary>
    internal static bool Suggested(SqliteConnection connection, SqliteTransaction transaction, string entity,
        string? before, string after, string trigger, string basis, string now) =>
        connection.Run(transaction, """
            INSERT INTO knowledge_revisions (entity_id, field, before_value, after_value, trigger, basis, state, created_utc)
            SELECT $entity, 'entity_type', $before, $after, $trigger, $basis, 'suggested', $now
            WHERE NOT EXISTS (
                SELECT 1 FROM knowledge_revisions
                WHERE entity_id = $entity AND field = 'entity_type' AND after_value = $after
                  AND state IN ('suggested', 'dismissed'))
            """, ("$entity", entity), ("$before", before), ("$after", after), ("$trigger", trigger),
            ("$basis", Clip(basis)), ("$now", now)) == 1;

    internal static KnowledgeRevision Read(SqliteDataReader reader) => new(
        reader.GetInt64(0), Guid.Parse(reader.GetString(1)), reader.GetString(2), reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7),
        reader.GetString(8), CatalogValues.ParseTimestamp(reader.GetString(9)),
        reader.IsDBNull(10) ? null : CatalogValues.ParseTimestamp(reader.GetString(10)));

    internal const string Columns = """
        r.revision_id, r.entity_id, e.display_name, r.field, r.before_value, r.after_value,
        r.trigger, r.basis, r.state, r.created_utc, r.decided_utc
        """;

    private static string Clip(string basis) => basis.Length <= 300 ? basis : basis[..299] + "…";
}
