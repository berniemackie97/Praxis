using Microsoft.Data.Sqlite;
using Praxis.Application.Knowledge;

namespace Praxis.Infrastructure.Persistence.Knowledge;

public sealed partial class SqliteGraphReader
{
    public GraphConceptPage ConceptRegions(GraphQuery query, CancellationToken cancellationToken = default)
    {
        Validate(query, allowEdges: false);
        if (query.EntityType is not null || query.IncludeSuggestions)
        {
            throw new ArgumentException("Concept regions require confirmed knowledge without an entity-type filter.", nameof(query));
        }

        return database.Execute(connection => ReadConceptRegions(connection, query, cancellationToken));
    }

    private GraphConceptPage ReadConceptRegions(SqliteConnection connection, GraphQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long version = Version(connection);
        string signature = Signature("concept-regions", null, null, query);
        int total = CountConceptRoots(connection, query);
        int offset = ReadConceptOffset(query, version, signature, total);
        if (total == 0)
        {
            return new GraphConceptPage(0, [], null);
        }

        List<(Guid Id, int Relations, int Related, int Contained)> rows =
            ReadConceptRows(connection, query, offset, cancellationToken);
        bool more = rows.Count > query.Limit;
        if (more)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        List<GraphConceptRegion> regions = [];
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphNode root = ReadNode(connection, row.Id, query) ??
                throw new InvalidOperationException("The concept region changed while reading.");
            regions.Add(new GraphConceptRegion(root.EntityId, root.Label,
                row.Relations, row.Related, row.Contained));
        }
        return new GraphConceptPage(total, regions,
            more ? WriteToken(version, signature, (offset + regions.Count).ToString()) : null);
    }

    private int ReadConceptOffset(GraphQuery query, long version, string signature, int total)
    {
        if (query.Cursor is null)
        {
            return 0;
        }

        string value = CursorValue(query.Cursor, version, signature);
        if (int.TryParse(value, out int offset) && offset >= query.Limit &&
            offset < total && offset % query.Limit == 0)
        {
            return offset;
        }
        throw new ArgumentException("The concept region cursor is invalid.", nameof(query));
    }

    private static int CountConceptRoots(SqliteConnection connection, GraphQuery query)
    {
        using SqliteCommand command = connection.CreateCommand($"""
            SELECT COUNT(*) FROM knowledge_entities e
            WHERE {NodeVisible("e", query)}
              AND {EffectiveType("e", query)} = 'project'
            """);
        Scope(command, query);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static List<(Guid Id, int Relations, int Related, int Contained)> ReadConceptRows(
        SqliteConnection connection, GraphQuery query, int offset, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand(ConceptRegionsSql(query));
        Scope(command, query);
        command.Parameters.AddWithValue("$suggestions", 0);
        command.Parameters.AddWithValue("$predicate", DBNull.Value);
        command.Parameters.AddWithValue("$evidenceStatus", DBNull.Value);
        command.Parameters.AddWithValue("$take", query.Limit + 1);
        command.Parameters.AddWithValue("$offset", offset);

        List<(Guid Id, int Relations, int Related, int Contained)> rows = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            rows.Add((Guid.Parse(reader.GetString(0)), reader.GetInt32(1),
                reader.GetInt32(2), reader.GetInt32(3)));
        }
        return rows;
    }

    private static string ConceptRegionsSql(GraphQuery query) => $"""
        WITH roots AS MATERIALIZED (
            SELECT e.entity_id, (
                SELECT COUNT(*) FROM confirmed_entity_mentions m
                WHERE m.entity_id = e.entity_id {ScopeFilter("m")}
            ) AS mentions
            FROM knowledge_entities e
            WHERE {NodeVisible("e", query)}
              AND {EffectiveType("e", query)} = 'project'
            ORDER BY mentions DESC, e.entity_id
            LIMIT $take OFFSET $offset
        ),
        related AS (
            SELECT roots.entity_id AS root_id,
                   CASE WHEN r.subject_id = roots.entity_id THEN r.object_id
                        ELSE r.subject_id END AS peer_id,
                   r.subject_id, r.object_id, r.predicate
            FROM roots
            JOIN knowledge_relations r ON
                r.subject_id = roots.entity_id OR r.object_id = roots.entity_id
            JOIN knowledge_entities subject ON subject.entity_id = r.subject_id
            JOIN knowledge_entities object ON object.entity_id = r.object_id
            LEFT JOIN eligible_enrichment_proposals p ON p.proposal_id = r.proposal_id
            LEFT JOIN proposal_verdicts v ON v.proposal_id = p.proposal_id
            WHERE {RelationPolicy()}
              AND {NodeVisible("subject", query)}
              AND {NodeVisible("object", query)}
        )
        SELECT roots.entity_id, COUNT(related.peer_id),
               COUNT(DISTINCT related.peer_id),
               COUNT(DISTINCT CASE
                   WHEN (related.subject_id = roots.entity_id AND related.predicate = 'contains')
                     OR (related.object_id = roots.entity_id AND related.predicate = 'part_of')
                   THEN related.peer_id END)
        FROM roots
        LEFT JOIN related ON related.root_id = roots.entity_id
        GROUP BY roots.entity_id, roots.mentions
        ORDER BY roots.mentions DESC, roots.entity_id
        """;
}
