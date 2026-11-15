using Praxis.Application.Knowledge;

namespace Praxis.Infrastructure.Persistence.Knowledge;

public sealed partial class SqliteGraphReader
{
    /// <summary>
    /// Common table expressions ending in <c>visible_kinds(entity_id, kind)</c>: every entity visible in
    /// the query's mention view and scope, with its effective type (owner type, else unanimous technical
    /// evidence, else the most frequent mention type with the work-item correction, else the stored type).
    /// Set-based equivalent of <see cref="NodeVisible"/> plus <see cref="EffectiveType"/>, computed once
    /// per query instead of once per row. <c>kind</c> may be null for an untyped entity.
    /// </summary>
    internal static string VisibleKinds(GraphQuery query) => $"""
        kind_mentions AS MATERIALIZED (
            SELECT m.entity_id, m.entity_type, COUNT(*) AS mentions
            FROM {MentionView(query)} m
            WHERE 1 = 1 {ScopeFilter("m")}
            GROUP BY m.entity_id, m.entity_type
        ),
        kind_technical AS MATERIALIZED (
            SELECT m.entity_id, MIN(m.entity_type) AS kind
            FROM visible_entity_mentions m
            WHERE m.processor_key = 'technical-evidence-v1' AND m.flags IS NULL {ScopeFilter("m")}
            GROUP BY m.entity_id HAVING COUNT(DISTINCT m.entity_type) = 1
        ),
        kind_frequent AS (
            SELECT entity_id, entity_type FROM (
                SELECT entity_id, entity_type,
                       ROW_NUMBER() OVER (PARTITION BY entity_id ORDER BY mentions DESC, entity_type) AS rank
                FROM kind_mentions WHERE entity_type IS NOT NULL)
            WHERE rank = 1
        ),
        visible_kinds AS MATERIALIZED (
            SELECT e.entity_id,
                   CASE WHEN e.type_origin <> 'owner' AND technical.kind IS NOT NULL THEN technical.kind
                        WHEN e.type_origin = 'inferred' THEN {EntityTypeSanity.CorrectInferred("frequent.entity_type", "e")}
                        ELSE e.entity_type END AS kind
            FROM knowledge_entities e
            JOIN (SELECT DISTINCT entity_id FROM kind_mentions) visible ON visible.entity_id = e.entity_id
            LEFT JOIN kind_technical technical ON technical.entity_id = e.entity_id
            LEFT JOIN kind_frequent frequent ON frequent.entity_id = e.entity_id
            WHERE e.status = 'active'
        )
        """;
}
