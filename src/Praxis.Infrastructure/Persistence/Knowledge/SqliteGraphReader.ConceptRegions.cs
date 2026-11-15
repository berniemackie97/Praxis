using Microsoft.Data.Sqlite;
using Praxis.Application.Knowledge;

namespace Praxis.Infrastructure.Persistence.Knowledge;

public sealed partial class SqliteGraphReader
{
    public GraphConceptPage ConceptRegions(GraphQuery query, CancellationToken cancellationToken = default,
        Guid? parentRegionId = null)
    {
        Validate(query, allowEdges: false);
        if (query.EntityType is not null || query.IncludeSuggestions || parentRegionId == Guid.Empty)
        {
            throw new ArgumentException("Concept regions require a valid parent and confirmed knowledge without a type filter.", nameof(query));
        }

        return database.ExecuteSnapshot(connection => ReadConceptRegions(connection, query, parentRegionId, cancellationToken));
    }

    private GraphConceptPage ReadConceptRegions(SqliteConnection connection, GraphQuery query,
        Guid? parentRegionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long version = Version(connection);
        string signature = Signature("concept-containment-v1", parentRegionId, null, query);
        int offset = ReadConceptOffset(query, version, signature);
        using SqliteCommand command = connection.CreateCommand(ConceptRegionsSql(query, parentRegionId.HasValue));
        Scope(command, query);
        command.Parameters.AddWithValue("$suggestions", 0);
        command.Parameters.AddWithValue("$predicate", DBNull.Value);
        command.Parameters.AddWithValue("$evidenceStatus", DBNull.Value);
        if (parentRegionId is Guid parent)
        {
            command.Parameters.AddWithValue("$parent", parent.ToString("D"));
        }
        command.Parameters.AddWithValue("$take", query.Limit + 1);
        command.Parameters.AddWithValue("$offset", offset);

        int total = 0;
        List<(Guid Id, int Count, int Owner, int Accepted, int Verified)> rows = [];
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                total = reader.GetInt32(1);
                if (!reader.IsDBNull(0))
                {
                    rows.Add((Guid.Parse(reader.GetString(0)), reader.GetInt32(2), reader.GetInt32(3),
                        reader.GetInt32(4), reader.GetInt32(5)));
                }
            }
        }

        if (query.Cursor is not null && offset >= total)
        {
            throw new ArgumentException("The concept region cursor is invalid.", nameof(query));
        }
        bool more = rows.Count > query.Limit;
        if (more)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        List<GraphConceptRegion> regions = [];
        Dictionary<Guid, GraphNode> roots = ReadNodes(connection, [.. rows.Select(row => row.Id)], query);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphNode root = roots.GetValueOrDefault(row.Id) ??
                throw new InvalidOperationException("The concept region changed while reading.");
            regions.Add(new GraphConceptRegion(root.EntityId, root.Label, row.Count,
                row.Owner, row.Accepted, row.Verified));
        }
        return new GraphConceptPage(total, regions,
            more ? WriteToken(version, signature, (offset + regions.Count).ToString()) : null, version);
    }

    private int ReadConceptOffset(GraphQuery query, long version, string signature)
    {
        if (query.Cursor is null)
        {
            return 0;
        }

        string value = CursorValue(query.Cursor, version, signature);
        if (int.TryParse(value, out int offset) && offset >= query.Limit && offset % query.Limit == 0)
        {
            return offset;
        }
        throw new ArgumentException("The concept region cursor is invalid.", nameof(query));
    }

    private static string ConceptRegionsSql(GraphQuery query, bool nested)
    {
        // A containment edge is a structural claim in its own right. General graph visibility,
        // document tier and technical extraction alone do not authorize geographic placement.
        // Walk backwards only on drill-down; UNION deduplicates visits even with cyclic claims.
        string ancestors = nested ? """
            ancestors(id) AS (
                SELECT $parent
                UNION
                SELECT c.parent_id FROM containment c JOIN ancestors a ON a.id = c.child_id
            ),
            """ : "";
        string placement = nested ? """
            EXISTS (SELECT 1 FROM containment c WHERE c.parent_id = $parent AND c.child_id = g.parent_id)
            AND NOT EXISTS (SELECT 1 FROM ancestors a WHERE a.id = g.parent_id)
            AND EXISTS (
                SELECT 1 FROM ancestors a
                WHERE NOT EXISTS (SELECT 1 FROM containment c WHERE c.child_id = a.id))
            """ : "NOT EXISTS (SELECT 1 FROM containment c WHERE c.child_id = g.parent_id)";
        return $"""
            WITH RECURSIVE containment AS MATERIALIZED (
                SELECT DISTINCT
                    CASE r.predicate WHEN 'contains' THEN r.subject_id ELSE r.object_id END AS parent_id,
                    CASE r.predicate WHEN 'contains' THEN r.object_id ELSE r.subject_id END AS child_id,
                    {RelationStatus} AS authority
                FROM knowledge_relations r
                JOIN knowledge_entities subject ON subject.entity_id = r.subject_id
                JOIN knowledge_entities object ON object.entity_id = r.object_id
                LEFT JOIN eligible_enrichment_proposals p ON p.proposal_id = r.proposal_id
                LEFT JOIN proposal_verdicts v ON v.proposal_id = p.proposal_id
                WHERE r.predicate IN ('contains', 'part_of')
                  AND r.subject_id <> r.object_id
                  AND (r.origin = 'owner' OR p.status IN ('accepted', 'corrected') OR v.verdict = 'supported')
                  AND {RelationPolicy()}
                  AND {NodeVisible("subject", query)}
                  AND {NodeVisible("object", query)}
            ),
            {ancestors}
            groups AS (
                SELECT parent_id, COUNT(DISTINCT child_id) AS members,
                    COUNT(DISTINCT CASE WHEN authority = 'owner' THEN child_id END) AS owner_members,
                    COUNT(DISTINCT CASE WHEN authority = 'accepted' THEN child_id END) AS accepted_members,
                    COUNT(DISTINCT CASE WHEN authority = 'verified' THEN child_id END) AS verified_members
                FROM containment
                GROUP BY parent_id
            ),
            regions AS MATERIALIZED (SELECT g.* FROM groups g WHERE {placement}),
            page AS (
                SELECT * FROM regions ORDER BY members DESC, parent_id LIMIT $take OFFSET $offset
            )
            SELECT page.parent_id, total.count, page.members, page.owner_members,
                   page.accepted_members, page.verified_members
            FROM (SELECT COUNT(*) AS count FROM regions) total
            LEFT JOIN page ON 1 = 1
            ORDER BY page.members DESC, page.parent_id
            """;
    }
}
