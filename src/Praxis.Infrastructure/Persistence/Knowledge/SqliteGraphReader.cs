using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Praxis.Application.Knowledge;
using Praxis.Domain.Knowledge;

namespace Praxis.Infrastructure.Persistence.Knowledge;

/// <summary>Bounded graph projection over the existing owner-local catalog.</summary>
public sealed partial class SqliteGraphReader : IGraphReader
{
    private readonly CatalogDatabase database;
    private readonly string session = Guid.NewGuid().ToString("N");

    internal SqliteGraphReader(CatalogDatabase database)
    {
        this.database = database;
    }

    internal const string RelationStatus = """
        CASE WHEN r.origin = 'owner' THEN 'owner'
             WHEN p.status IN ('accepted', 'corrected') THEN 'accepted'
             WHEN v.verdict = 'unsupported' THEN 'contradicted'
             WHEN v.verdict = 'supported' THEN 'verified'
             WHEN p.processor_key = 'technical-evidence-v1' THEN 'documented'
             WHEN p.processor_key = 'intake-understand-v1' AND EXISTS (
                  SELECT 1 FROM intake_documents d WHERE d.item_id = p.item_id
                    AND d.revision = p.revision AND d.tier IN ('strong', 'strongest', 'reused')
             ) THEN 'documented'
             ELSE 'suggested' END
        """;

    private const string ConfirmedRelation = """
        (r.origin = 'owner' OR
         (p.proposal_id IS NOT NULL AND p.status <> 'rejected'
          AND (p.status IN ('accepted', 'corrected') OR
               (v.verdict = 'supported') OR p.processor_key = 'technical-evidence-v1' OR
               (p.processor_key = 'intake-understand-v1' AND EXISTS (
                    SELECT 1 FROM intake_documents d
                    WHERE d.item_id = p.item_id AND d.revision = p.revision
                      AND d.tier IN ('strong', 'strongest', 'reused'))))
          AND (p.status IN ('accepted', 'corrected') OR
               (COALESCE(v.verdict, '') <> 'unsupported' AND
                (p.flags IS NULL OR v.verdict = 'supported')))))
        """;

    public GraphOverview Overview(GraphQuery query, CancellationToken cancellationToken = default)
    {
        Validate(query, allowEdges: false);
        if (query.Cursor is not null || query.EntityType is not null)
        {
            throw new ArgumentException("Overview accepts a source and collection scope, not a page or type filter.", nameof(query));
        }
        return database.ExecuteSnapshot(connection => ReadOverview(connection, query, cancellationToken));
    }

    public GraphZoneOverview Zones(GraphQuery query, CancellationToken cancellationToken = default)
    {
        Validate(query, allowEdges: true);
        if (query.Cursor is not null || query.EntityType is not null)
        {
            throw new ArgumentException("Zone overview does not accept a cursor or entity type.", nameof(query));
        }

        return database.ExecuteSnapshot(connection =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            GraphOverview overview = ReadOverview(connection, query, cancellationToken);
            using SqliteCommand command = connection.CreateCommand($"""
                WITH {VisibleKinds(query)},
                visible_entities AS (SELECT entity_id, COALESCE(kind, 'other') AS kind FROM visible_kinds),
                -- Materialized so the type grouping below cannot reorder the join into a full scan per entity.
                visible_relations AS MATERIALIZED (
                    SELECT subject.kind AS subject_type, object.kind AS object_type,
                           {RelationStatus} AS status
                    FROM knowledge_relations r
                    JOIN visible_entities subject ON subject.entity_id = r.subject_id
                    JOIN visible_entities object ON object.entity_id = r.object_id
                    LEFT JOIN eligible_enrichment_proposals p ON p.proposal_id = r.proposal_id
                    LEFT JOIN proposal_verdicts v ON v.proposal_id = p.proposal_id
                    WHERE {RelationPolicy()}
                )
                SELECT subject_type, object_type,
                       SUM(CASE WHEN status NOT IN ('suggested', 'contradicted') THEN 1 ELSE 0 END),
                       SUM(CASE WHEN status = 'suggested' THEN 1 ELSE 0 END),
                       SUM(CASE WHEN status = 'contradicted' THEN 1 ELSE 0 END)
                FROM visible_relations
                GROUP BY subject_type, object_type
                ORDER BY subject_type, object_type
                """);
            Scope(command, query);
            command.Parameters.AddWithValue("$suggestions", query.IncludeSuggestions ? 1 : 0);
            command.Parameters.AddWithValue("$predicate", (object?)query.Predicate ?? DBNull.Value);
            command.Parameters.AddWithValue("$evidenceStatus", (object?)query.EvidenceStatus ?? DBNull.Value);
            List<GraphZoneLink> links = [];
            int relationships = 0;
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                int confirmed = reader.GetInt32(2);
                int suggested = reader.GetInt32(3);
                int contradicted = reader.GetInt32(4);
                relationships += confirmed + suggested + contradicted;
                links.Add(new GraphZoneLink(reader.GetString(0), reader.GetString(1),
                    confirmed, suggested, contradicted));
            }
            return new GraphZoneOverview(overview.EntityCount, relationships, overview.Types, links);
        });
    }

    private static GraphOverview ReadOverview(SqliteConnection connection, GraphQuery query,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteCommand command = connection.CreateCommand($"""
            WITH {VisibleKinds(query)}
            SELECT COALESCE(kind, 'other') AS kind, COUNT(*) AS count FROM visible_kinds
            GROUP BY COALESCE(kind, 'other')
            ORDER BY count DESC, kind
            """);
        Scope(command, query);
        List<GraphTypeCount> types = [];
        int total = 0;
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = reader.GetInt32(1);
            types.Add(new GraphTypeCount(reader.GetString(0), count));
            total += count;
        }
        return new GraphOverview(total, types);
    }

    public GraphPage Seeds(string? search, GraphQuery query, CancellationToken cancellationToken = default)
    {
        Validate(query, allowEdges: false);
        string? nameKey = search is null ? null : KnowledgeVocabulary.NameKey(search);
        if (search is not null && (search.Length > 120 || nameKey is null))
        {
            throw new ArgumentException("The graph search is invalid.", nameof(search));
        }

        return database.ExecuteSnapshot(connection =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            long version = Version(connection);
            string signature = Signature("seeds", null, nameKey, query);
            string after = ReadCursor(query.Cursor, version, signature);
            List<Guid> ids = [];
            using (SqliteCommand command = connection.CreateCommand($"""
                SELECT e.entity_id
                FROM knowledge_entities e
                WHERE {NodeVisible("e", query)}
                  AND ($type IS NULL OR {EffectiveType("e", query)} = $type)
                  AND ($search IS NULL OR EXISTS (
                      SELECT 1 FROM {MentionView(query)} m
                      JOIN eligible_enrichment_proposals p ON p.proposal_id = m.proposal_id
                      WHERE m.entity_id = e.entity_id {ScopeFilter("m")}
                        AND instr(upper(CASE WHEN p.status = 'corrected' THEN p.correction ELSE p.value END),
                                  $search) > 0
                  ) OR (instr(upper(e.display_name), $search) > 0
                     AND EXISTS (SELECT 1 FROM entity_decisions d
                                 WHERE d.entity_id = e.entity_id AND d.action = 'rename')))
                  AND e.entity_id > $after
                ORDER BY e.entity_id
                LIMIT $take
                """))
            {
                Scope(command, query);
                command.Parameters.AddWithValue("$type", (object?)query.EntityType ?? DBNull.Value);
                command.Parameters.AddWithValue("$search", (object?)nameKey ?? DBNull.Value);
                command.Parameters.AddWithValue("$after", after);
                command.Parameters.AddWithValue("$take", query.Limit + 1);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ids.Add(Guid.Parse(reader.GetString(0)));
                }
            }

            bool more = ids.Count > query.Limit;
            if (more)
            {
                ids.RemoveAt(ids.Count - 1);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<Guid, GraphNode> read = ReadNodes(connection, ids, query);
            List<GraphNode> nodes = [.. ids.Where(read.ContainsKey).Select(id => read[id])];

            return new GraphPage(nodes.Count == 0 ? "empty" : "ready", null, nodes, [],
                more ? WriteCursor(version, signature, ids[^1]) : null);
        });
    }

    public GraphPage Atlas(GraphQuery query, CancellationToken cancellationToken = default) =>
        ReadEdges(null, null, query, cancellationToken);

    private const int RegionEdgeBudget = 600;

    public GraphPage Region(GraphQuery query, CancellationToken cancellationToken = default)
    {
        Validate(query, allowEdges: true);
        if (query.EntityType is null)
        {
            throw new ArgumentException("A region requires an entity type.", nameof(query));
        }

        return database.ExecuteSnapshot(connection =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            long version = Version(connection);
            string signature = Signature("region", null, null, query);
            int offset = ReadRegionCursor(query.Cursor, version, signature, query.Limit);
            List<EdgeRow> rows = SqliteGraphRegionQuery.Read(connection, query, offset,
                Math.Min(query.Limit, RegionEdgeBudget - offset) + 1, cancellationToken);

            int pageLimit = Math.Min(query.Limit, RegionEdgeBudget - offset);
            bool more = rows.Count > pageLimit;
            if (more)
            {
                rows.RemoveAt(rows.Count - 1);
            }
            string? nextCursor = more && offset + rows.Count < RegionEdgeBudget
                ? WriteRegionCursor(version, signature, offset + rows.Count) : null;
            return ComposePage(connection, query, rows, null, null, null, nextCursor, cancellationToken);
        });
    }


    public GraphPage Neighborhood(Guid focusId, GraphQuery query, CancellationToken cancellationToken = default) =>
        ReadEdges(focusId, null, query, cancellationToken);

    public GraphPage Relation(Guid focusId, Guid relationId, GraphQuery query, CancellationToken cancellationToken = default)
    {
        if (relationId == Guid.Empty || query.Cursor is not null)
        {
            throw new ArgumentException("A valid relationship and no cursor are required.");
        }

        Validate(query, allowEdges: true);
        return ReadEdges(focusId, relationId, query with { Limit = 1 }, cancellationToken);
    }

    private GraphPage ReadEdges(Guid? focusId, Guid? relationId, GraphQuery query,
        CancellationToken cancellationToken)
    {
        if (focusId == Guid.Empty)
        {
            throw new ArgumentException("A valid graph focus is required.", nameof(focusId));
        }
        Validate(query, allowEdges: true);
        return database.ExecuteSnapshot(connection =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            long version = Version(connection);
            string signature = Signature(focusId is null ? "atlas" : relationId is null ? "neighbors" : "relation",
                focusId, relationId?.ToString("D"), query);
            // The whole-brain atlas pages runtime glue after every other link: (glue, relation_id) keyset.
            (long afterGlue, string after) = focusId is null ? ReadAtlasCursor(query.Cursor, version, signature)
                : (0, ReadCursor(query.Cursor, version, signature));
            GraphNode? root = focusId is Guid rootId ? ReadNode(connection, rootId, query) : null;
            if (focusId is not null && root is null)
            {
                if (query.Cursor is not null)
                {
                    throw new InvalidOperationException("The graph page is no longer current.");
                }
                return new GraphPage("unavailable", null, [], [], null);
            }

            List<EdgeRow> rows = [];
            List<long> glues = [];
            // Each glue rank is its own keyset scan in relation order, so a page stops early instead of
            // sorting every visible link; glue links follow only once regular links are exhausted.
            for (long rank = afterGlue; rank <= (focusId is null ? 1 : 0) && rows.Count <= query.Limit; rank++)
            {
                using SqliteCommand command = connection.CreateCommand($"""
                SELECT r.relation_id, r.subject_id, r.object_id, r.predicate, r.origin,
                       {RelationStatus} AS evidence_status,
                       p.proposal_id, p.source_id, p.item_id, p.revision, p.source_digest,
                       s.locator, p.evidence, p.evidence_offset, p.status, v.verdict,
                       CASE WHEN $focus IS NULL THEN {AtlasGlue} ELSE 0 END AS glue
                FROM knowledge_relations r
                JOIN knowledge_entities subject ON subject.entity_id = r.subject_id
                JOIN knowledge_entities object ON object.entity_id = r.object_id
                LEFT JOIN eligible_enrichment_proposals p ON p.proposal_id = r.proposal_id
                LEFT JOIN proposal_verdicts v ON v.proposal_id = p.proposal_id
                LEFT JOIN sources s ON s.source_id = p.source_id
                WHERE ($focus IS NULL OR r.subject_id = $focus OR r.object_id = $focus)
                  AND ($relationId IS NULL OR r.relation_id = $relationId)
                  AND (CASE WHEN $focus IS NULL THEN {AtlasGlue} ELSE 0 END) = $glueRank
                  AND r.relation_id > $after
                  AND {RelationPolicy()}
                  AND {NodeVisible("subject", query)}
                  AND {NodeVisible("object", query)}
                  AND ($type IS NULL OR
                      ($focus IS NULL AND ({EffectiveType("subject", query)} = $type OR {EffectiveType("object", query)} = $type))
                      OR ($focus IS NOT NULL AND
                          (CASE WHEN r.subject_id = $focus THEN {EffectiveType("object", query)}
                                ELSE {EffectiveType("subject", query)} END) = $type))
                ORDER BY r.relation_id
                LIMIT $take
                """);
                Scope(command, query);
                command.Parameters.AddWithValue("$focus", (object?)focusId?.ToString("D") ?? DBNull.Value);
                command.Parameters.AddWithValue("$relationId", (object?)relationId?.ToString("D") ?? DBNull.Value);
                command.Parameters.AddWithValue("$suggestions", query.IncludeSuggestions ? 1 : 0);
                command.Parameters.AddWithValue("$after", rank == afterGlue ? after : "");
                command.Parameters.AddWithValue("$glueRank", rank);
                command.Parameters.AddWithValue("$predicate", (object?)query.Predicate ?? DBNull.Value);
                command.Parameters.AddWithValue("$evidenceStatus", (object?)query.EvidenceStatus ?? DBNull.Value);
                command.Parameters.AddWithValue("$type", (object?)query.EntityType ?? DBNull.Value);
                command.Parameters.AddWithValue("$take", query.Limit + 1 - rows.Count);
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rows.Add(ReadEdgeRow(reader));
                    glues.Add(reader.GetInt64(16));
                }
            }

            bool more = rows.Count > query.Limit;
            if (more)
            {
                rows.RemoveAt(rows.Count - 1);
            }
            string? next = !more ? null : focusId is null
                ? WriteToken(version, signature, $"{glues[rows.Count - 1]}:{rows[^1].RelationId:D}")
                : WriteCursor(version, signature, rows[^1].RelationId);
            return ComposePage(connection, query, rows, focusId, relationId, root, next, cancellationToken);
        });
    }

    internal static EdgeRow ReadEdgeRow(SqliteDataReader reader)
    {
        GraphEvidence? evidence = reader.IsDBNull(6) ? null : new GraphEvidence(
            Guid.Parse(reader.GetString(6)), Guid.Parse(reader.GetString(7)),
            Guid.Parse(reader.GetString(8)), reader.GetInt32(9), reader.GetString(10),
            reader.GetString(11), reader.GetString(12), reader.GetInt32(13),
            reader.GetString(14), reader.IsDBNull(15) ? null : reader.GetString(15));
        return new EdgeRow(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
            Guid.Parse(reader.GetString(2)), reader.GetString(3), reader.GetString(4),
            reader.GetString(5), evidence);
    }

    private static GraphPage ComposePage(SqliteConnection connection, GraphQuery query,
        IReadOnlyList<EdgeRow> rows, Guid? focusId, Guid? relationId, GraphNode? root,
        string? nextCursor, CancellationToken cancellationToken)
    {
        List<GraphNode> nodes = [];
        List<GraphEdge> edges = [];
        HashSet<Guid> known = [];
        if (focusId is Guid knownFocusId && root is not null)
        {
            nodes.Add(root);
            known.Add(knownFocusId);
        }
        List<Guid> order = [];
        foreach (EdgeRow row in rows)
        {
            if (known.Add(row.SubjectId))
            {
                order.Add(row.SubjectId);
            }
            if (known.Add(row.ObjectId))
            {
                order.Add(row.ObjectId);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<Guid, GraphNode> read = ReadNodes(connection, order, query);
        foreach (Guid id in order)
        {
            if (read.TryGetValue(id, out GraphNode? node))
            {
                nodes.Add(node);
            }
        }

        foreach (EdgeRow row in rows)
        {
            edges.Add(new GraphEdge(row.RelationId, row.SubjectId, row.ObjectId,
                row.Predicate, row.Origin, row.EvidenceStatus, row.Evidence,
                row.Evidence is null ? ["inspectRelation"] : ["inspectRelation", "openSource"]));
        }
        string outcome = relationId is not null && edges.Count == 0 ? "unavailable" :
            focusId is null && edges.Count == 0 ? "empty" : "ready";
        return new GraphPage(outcome, focusId,
            relationId is not null && edges.Count == 0 ? [] : nodes, edges, nextCursor);
    }

    internal static string RelationPolicy() => $"""
        ({ConfirmedRelation} OR ($suggestions = 1
            AND r.origin = 'model' AND p.proposal_id IS NOT NULL
            AND p.status <> 'rejected' AND p.flags IS NULL
            AND (v.verdict = 'unsupported' OR
                 (p.status = 'pending'
                  AND COALESCE(v.verdict, '') IN ('', 'uncertain')))))
        AND ($predicate IS NULL OR r.predicate = $predicate)
        AND ($evidenceStatus IS NULL OR {RelationStatus} = $evidenceStatus)
        AND ($source IS NULL OR r.origin = 'owner' OR p.source_id = $source)
        AND ($collection IS NULL OR r.origin = 'owner' OR EXISTS (
            SELECT 1 FROM source_collection_memberships cm
            JOIN source_collections c ON c.collection_id = cm.collection_id
            WHERE cm.source_id = p.source_id
              AND cm.collection_id = $collection AND c.archived_utc IS NULL))
        """;

    private static string ScopeFilter(string alias) => $"""
        AND ($source IS NULL OR {alias}.source_id = $source)
        AND ($collection IS NULL OR EXISTS (
            SELECT 1 FROM source_collection_memberships cm
            JOIN source_collections c ON c.collection_id = cm.collection_id
            WHERE cm.source_id = {alias}.source_id AND cm.collection_id = $collection
              AND c.archived_utc IS NULL))
        """;

    private static string MentionView(GraphQuery query) =>
        query.IncludeSuggestions ? "visible_entity_mentions" : "confirmed_entity_mentions";

    internal static string NodeVisible(string alias, GraphQuery query) => $"""
        {alias}.status = 'active' AND EXISTS (
            SELECT 1 FROM {MentionView(query)} scoped
            WHERE scoped.entity_id = {alias}.entity_id
              {ScopeFilter("scoped")}
        )
        """;

    private static string TechnicalType(string alias) => $"""
        (SELECT MIN(m.entity_type) FROM visible_entity_mentions m
         WHERE m.entity_id = {alias}.entity_id AND m.processor_key = 'technical-evidence-v1' AND m.flags IS NULL
           {ScopeFilter("m")}
         HAVING COUNT(DISTINCT m.entity_type) = 1)
        """;

    internal static string EffectiveType(string alias, GraphQuery query) => $"""
        CASE WHEN {alias}.type_origin <> 'owner' AND {TechnicalType(alias)} IS NOT NULL THEN {TechnicalType(alias)}
        WHEN {alias}.type_origin = 'inferred' THEN (
            SELECT {EntityTypeSanity.CorrectInferred("ranked.entity_type", alias)}
            FROM (SELECT m.entity_type FROM {MentionView(query)} m
                  WHERE m.entity_id = {alias}.entity_id AND m.entity_type IS NOT NULL
                    {ScopeFilter("m")}
                  GROUP BY m.entity_type ORDER BY COUNT(*) DESC, m.entity_type LIMIT 1) ranked)
        ELSE {alias}.entity_type END
        """;

    private static GraphNode? ReadNode(SqliteConnection connection, Guid id, GraphQuery query) =>
        ReadNodes(connection, [id], query).GetValueOrDefault(id);

    internal static void Scope(SqliteCommand command, GraphQuery query)
    {
        command.Parameters.AddWithValue("$source", (object?)query.SourceId?.ToString("D") ?? DBNull.Value);
        command.Parameters.AddWithValue("$collection", (object?)query.CollectionId?.ToString("D") ?? DBNull.Value);
    }

    private static void Validate(GraphQuery query, bool allowEdges)
    {
        if (query.Limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "The graph page limit must be between 1 and 100.");
        }
        if (query.SourceId == Guid.Empty || query.CollectionId == Guid.Empty)
        {
            throw new ArgumentException("A valid graph scope is required.", nameof(query));
        }
        if (query.EntityType is not null && !KnowledgeVocabulary.ValidIdentifier(query.EntityType))
        {
            throw new ArgumentException("The graph entity type is invalid.", nameof(query));
        }
        if (query.Predicate is not null && (!allowEdges || !KnowledgeVocabulary.ValidIdentifier(query.Predicate)))
        {
            throw new ArgumentException("The graph relation predicate is invalid.", nameof(query));
        }
        if (query.EvidenceStatus is not null && (!allowEdges ||
            query.EvidenceStatus is not ("owner" or "accepted" or "verified" or "documented" or "suggested" or "contradicted") ||
            ((query.EvidenceStatus is "suggested" or "contradicted") && !query.IncludeSuggestions)))
        {
            throw new ArgumentException("The graph evidence filter is invalid.", nameof(query));
        }
        if (query.Cursor is { Length: > 256 })
        {
            throw new ArgumentException("The graph cursor is invalid.", nameof(query));
        }
    }

    /// <summary>
    /// The knowledge revision: changes only when something the graph can show changes (mentions,
    /// relations, entities, reviews, verdicts, source status or scope), not on every catalog write.
    /// </summary>
    internal static long Version(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand("SELECT revision FROM graph_revision WHERE singleton = 1");
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static string Signature(string mode, Guid? focus, string? search, GraphQuery query)
    {
        string key = string.Join("|", mode, focus?.ToString("D"), search,
            query.CollectionId?.ToString("D"), query.SourceId?.ToString("D"),
            query.EntityType, query.Predicate, query.EvidenceStatus, query.Limit, query.IncludeSuggestions);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];
    }

    private string ReadCursor(string? cursor, long version, string signature)
    {
        if (cursor is null)
        {
            return "";
        }
        string value = CursorValue(cursor, version, signature);
        if (Guid.TryParseExact(value, "D", out Guid id) && id != Guid.Empty)
        {
            return id.ToString("D");
        }
        throw new ArgumentException("The graph cursor is invalid.", nameof(cursor));
    }

    private static readonly string AtlasGlue =
        $"MAX({EntityTypeSanity.RuntimeGlueName("subject")}, {EntityTypeSanity.RuntimeGlueName("object")})";

    /// <summary>Atlas cursors carry the glue rank and relation id of the last link shown.</summary>
    private (long Glue, string After) ReadAtlasCursor(string? cursor, long version, string signature)
    {
        if (cursor is null)
        {
            return (0, "");
        }

        string[] parts = CursorValue(cursor, version, signature).Split(':');
        if (parts.Length == 2 && parts[0] is "0" or "1" && Guid.TryParseExact(parts[1], "D", out Guid id) && id != Guid.Empty)
        {
            return (long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), id.ToString("D"));
        }

        throw new ArgumentException("The graph cursor is invalid.", nameof(cursor));
    }

    private int ReadRegionCursor(string? cursor, long version, string signature, int pageSize)
    {
        if (cursor is null)
        {
            return 0;
        }
        string value = CursorValue(cursor, version, signature);
        if (int.TryParse(value, out int offset) && offset is > 0 and < RegionEdgeBudget &&
            offset % pageSize == 0)
        {
            return offset;
        }
        throw new ArgumentException("The graph cursor is invalid.", nameof(cursor));
    }

    private string CursorValue(string cursor, long version, string signature)
    {
        try
        {
            string encoded = cursor.Replace('-', '+').Replace('_', '/');
            string raw = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
            string[] fields = raw.Split('|');
            if (fields.Length == 4 && fields[0] == session && fields[2] == signature &&
                long.TryParse(fields[1], out long observed))
            {
                if (observed != version)
                {
                    throw new InvalidOperationException("The graph page is no longer current.");
                }
                return fields[3];
            }
        }
        catch (FormatException)
        {
        }
        throw new ArgumentException("The graph cursor is invalid.", nameof(cursor));
    }

    private string WriteCursor(long version, string signature, Guid id) =>
        WriteToken(version, signature, id.ToString("D"));

    private string WriteRegionCursor(long version, string signature, int offset) =>
        WriteToken(version, signature, offset.ToString());

    private string WriteToken(long version, string signature, string value)
    {
        string raw = $"{session}|{version}|{signature}|{value}";
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    internal sealed record EdgeRow(Guid RelationId, Guid SubjectId, Guid ObjectId, string Predicate,
        string Origin, string EvidenceStatus, GraphEvidence? Evidence);
}
