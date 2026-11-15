using Microsoft.Data.Sqlite;
using Praxis.Application.Knowledge;

namespace Praxis.Infrastructure.Persistence.Knowledge;

public sealed partial class SqliteGraphReader
{
    public GraphClassificationAuditPage ClassificationAudit(GraphQuery query, CancellationToken cancellationToken = default)
    {
        Validate(query, allowEdges: false);
        if (query.EntityType is not null || query.IncludeSuggestions)
        {
            throw new ArgumentException("The audit matches the previous confirmed project filter.", nameof(query));
        }

        return database.Execute(connection =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            long revision = Version(connection);
            string signature = Signature("classification-audit-v1", null, null, query);
            string after = ReadCursor(query.Cursor, revision, signature);
            using SqliteCommand command = connection.CreateCommand($"""
                WITH candidates AS MATERIALIZED (
                    SELECT e.entity_id FROM knowledge_entities e
                    WHERE {NodeVisible("e", query)} AND {EffectiveType("e", query)} = 'project'
                ),
                page AS MATERIALIZED (
                    SELECT entity_id FROM candidates WHERE entity_id > $after ORDER BY entity_id LIMIT $take
                ),
                observations AS (
                    SELECT m.entity_id, m.proposal_id,
                        CASE WHEN m.status IN ('accepted', 'corrected') THEN 'accepted'
                             WHEN m.verdict = 'supported' THEN 'verified'
                             WHEN m.processor_key = 'technical-evidence-v1' THEN 'technical'
                             ELSE 'derived' END AS authority
                    FROM page JOIN confirmed_entity_mentions m ON m.entity_id = page.entity_id
                    WHERE m.entity_type = 'project' {ScopeFilter("m")}
                ),
                counts AS (
                    SELECT entity_id,
                        COUNT(DISTINCT CASE WHEN authority = 'accepted' THEN proposal_id END) AS accepted,
                        COUNT(DISTINCT CASE WHEN authority = 'verified' THEN proposal_id END) AS verified,
                        COUNT(DISTINCT CASE WHEN authority = 'technical' THEN proposal_id END) AS technical,
                        COUNT(DISTINCT CASE WHEN authority = 'derived' THEN proposal_id END) AS derived
                    FROM observations GROUP BY entity_id
                )
                SELECT total.count, page.entity_id, counts.accepted, counts.verified, counts.technical, counts.derived
                FROM (SELECT COUNT(*) AS count FROM candidates) total
                LEFT JOIN page ON 1 = 1
                LEFT JOIN counts ON counts.entity_id = page.entity_id
                ORDER BY page.entity_id
                """);
            Scope(command, query);
            command.Parameters.AddWithValue("$after", after);
            command.Parameters.AddWithValue("$take", query.Limit + 1);

            int total = 0;
            List<(Guid Id, int Accepted, int Verified, int Technical, int Derived)> rows = [];
            using (SqliteDataReader reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    total = reader.GetInt32(0);
                    if (!reader.IsDBNull(1))
                    {
                        rows.Add((Guid.Parse(reader.GetString(1)), Count(2), Count(3), Count(4), Count(5)));
                    }

                    int Count(int index) => reader.IsDBNull(index) ? 0 : reader.GetInt32(index);
                }
            }

            bool more = rows.Count > query.Limit;
            if (more)
            {
                rows.RemoveAt(rows.Count - 1);
            }
            List<GraphClassificationAuditEntry> entries = [];
            foreach (var row in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                GraphNode entity = ReadNode(connection, row.Id, query) ??
                    throw new InvalidOperationException("The classification changed while reading.");
                entries.Add(new GraphClassificationAuditEntry(entity, row.Accepted, row.Verified, row.Technical, row.Derived));
            }
            return new GraphClassificationAuditPage(total, entries,
                more ? WriteCursor(revision, signature, rows[^1].Id) : null, revision);
        });
    }
}
