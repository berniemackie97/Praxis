using Microsoft.Data.Sqlite;
using Praxis.Application.Knowledge;

namespace Praxis.Infrastructure.Persistence.Knowledge;

internal static class SqliteGraphRegionQuery
{
    internal static List<SqliteGraphReader.EdgeRow> Read(SqliteConnection connection, GraphQuery query,
        int offset, int take, CancellationToken cancellationToken)
    {
        List<SqliteGraphReader.EdgeRow> rows = [];
        using (SqliteCommand command = connection.CreateCommand($"""
            WITH eligible AS MATERIALIZED (
                SELECT r.relation_id, r.subject_id, r.object_id,
                       {SqliteGraphReader.RelationStatus} AS evidence_status,
                       {EntityTypeSanity.RuntimeGlueName("subject")} AS subject_glue,
                       {EntityTypeSanity.RuntimeGlueName("object")} AS object_glue
                FROM knowledge_relations r
                JOIN knowledge_entities subject ON subject.entity_id = r.subject_id
                JOIN knowledge_entities object ON object.entity_id = r.object_id
                LEFT JOIN eligible_enrichment_proposals p ON p.proposal_id = r.proposal_id
                LEFT JOIN proposal_verdicts v ON v.proposal_id = p.proposal_id
                WHERE {SqliteGraphReader.RelationPolicy()}
                  AND {SqliteGraphReader.NodeVisible("subject", query)}
                  AND {SqliteGraphReader.NodeVisible("object", query)}
                  AND ({SqliteGraphReader.EffectiveType("subject", query)} = $type
                       OR {SqliteGraphReader.EffectiveType("object", query)} = $type)
            ),
            -- Runtime glue keeps its relations but never anchors a hub: its degree counts as zero.
            degrees AS MATERIALIZED (
                SELECT entity_id, CASE WHEN MAX(glue) = 1 THEN 0 ELSE COUNT(*) END AS degree FROM (
                    SELECT subject_id AS entity_id, subject_glue AS glue FROM eligible
                    UNION ALL
                    SELECT object_id AS entity_id, object_glue AS glue FROM eligible
                ) GROUP BY entity_id
            ),
            anchored AS (
                SELECT eligible.relation_id, eligible.evidence_status,
                       MAX(eligible.subject_glue, eligible.object_glue) AS glue,
                       MAX(subject_degree.degree, object_degree.degree) AS hub_degree,
                       CASE WHEN subject_degree.degree > object_degree.degree THEN eligible.subject_id
                            WHEN subject_degree.degree < object_degree.degree THEN eligible.object_id
                            WHEN eligible.subject_id < eligible.object_id THEN eligible.subject_id
                            ELSE eligible.object_id END AS hub_id
                FROM eligible
                JOIN degrees subject_degree ON subject_degree.entity_id = eligible.subject_id
                JOIN degrees object_degree ON object_degree.entity_id = eligible.object_id
            ),
            ranked AS (
                SELECT relation_id, hub_degree, glue,
                       ROW_NUMBER() OVER (
                           PARTITION BY hub_id
                           ORDER BY CASE WHEN evidence_status IN ('suggested', 'contradicted')
                                         THEN 1 ELSE 0 END,
                                    relation_id) AS depth
                FROM anchored
            ),
            selected AS MATERIALIZED (
                SELECT relation_id, hub_degree, depth, glue FROM ranked
                ORDER BY glue, depth, hub_degree DESC, relation_id
                LIMIT $take OFFSET $offset
            )
            SELECT r.relation_id, r.subject_id, r.object_id, r.predicate, r.origin,
                   {SqliteGraphReader.RelationStatus} AS evidence_status,
                   p.proposal_id, p.source_id, p.item_id, p.revision, p.source_digest,
                   s.locator, p.evidence, p.evidence_offset, p.status, v.verdict
            FROM selected
            JOIN knowledge_relations r ON r.relation_id = selected.relation_id
            LEFT JOIN eligible_enrichment_proposals p ON p.proposal_id = r.proposal_id
            LEFT JOIN proposal_verdicts v ON v.proposal_id = p.proposal_id
            LEFT JOIN sources s ON s.source_id = p.source_id
            ORDER BY selected.glue, selected.depth, selected.hub_degree DESC, selected.relation_id
            """))
        {
            SqliteGraphReader.Scope(command, query);
            command.Parameters.AddWithValue("$suggestions", query.IncludeSuggestions ? 1 : 0);
            command.Parameters.AddWithValue("$predicate", (object?)query.Predicate ?? DBNull.Value);
            command.Parameters.AddWithValue("$evidenceStatus", (object?)query.EvidenceStatus ?? DBNull.Value);
            command.Parameters.AddWithValue("$type", query.EntityType);
            command.Parameters.AddWithValue("$take", take);
            command.Parameters.AddWithValue("$offset", offset);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(SqliteGraphReader.ReadEdgeRow(reader));
            }
        }

        return rows;
    }
}
