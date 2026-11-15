using Microsoft.Data.Sqlite;
using Praxis.Application.Knowledge;
using Praxis.Domain.Knowledge;

namespace Praxis.Infrastructure.Persistence.Knowledge;

public sealed partial class SqliteGraphReader
{
    private const int MaximumTopRegions = 48;
    private const int MaximumChildRegions = 24;
    private const int TopHubCount = 12;
    private const int ChildHubCount = 6;
    private readonly Lock regionGate = new();
    private (long Revision, string Signature, RegionModel Model)? regionCache;

    public GraphRegionMap KnowledgeRegions(GraphQuery query, CancellationToken cancellationToken = default)
    {
        Validate(query, allowEdges: false);
        if (query.Cursor is not null || query.EntityType is not null)
        {
            throw new ArgumentException("Knowledge regions accept a source and collection scope, not a page or type filter.", nameof(query));
        }

        return database.ExecuteSnapshot(connection => Regions(connection, query, cancellationToken).Map);
    }

    public GraphPage RegionMembers(string regionId, GraphQuery query, CancellationToken cancellationToken = default)
    {
        Validate(query, allowEdges: false);
        if (query.EntityType is not null)
        {
            throw new ArgumentException("Region members do not accept a type filter.", nameof(query));
        }

        return database.ExecuteSnapshot(connection =>
        {
            RegionModel model = Regions(connection, query, cancellationToken);
            if (!model.Members.TryGetValue(regionId, out List<Guid>? members))
            {
                return new GraphPage("unavailable", null, [], [], null);
            }

            long version = Version(connection);
            string signature = Signature("region-members:" + regionId, null, null, query);
            int offset = 0;
            if (query.Cursor is not null && (!int.TryParse(CursorValue(query.Cursor, version, signature), out offset)
                                             || offset <= 0 || offset >= members.Count))
            {
                throw new ArgumentException("The graph cursor is invalid.", nameof(query));
            }

            List<Guid> page = [.. members.Skip(offset).Take(query.Limit)];
            HashSet<Guid> inPage = [.. page];
            Dictionary<Guid, GraphNode> nodes = ReadNodes(connection, page, query);
            List<string> relationIds = [.. model.Relations
                .Where(relation => inPage.Contains(relation.Subject) && inPage.Contains(relation.Object))
                .Select(relation => relation.Id.ToString("D"))
                .Take(RegionEdgeBudget)];
            List<GraphEdge> edges = [.. ReadEdgesById(connection, relationIds, query).Select(row => new GraphEdge(
                row.RelationId, row.SubjectId, row.ObjectId, row.Predicate, row.Origin, row.EvidenceStatus, row.Evidence,
                row.Evidence is null ? ["inspectRelation"] : ["inspectRelation", "openSource"]))];
            string? next = offset + page.Count < members.Count ? WriteToken(version, signature, (offset + page.Count).ToString()) : null;
            List<GraphNode> ordered = [.. page.Where(nodes.ContainsKey).Select(id => nodes[id])];
            return new GraphPage(ordered.Count == 0 ? "empty" : "ready", null, ordered, edges, next);
        });
    }

    private sealed record RegionRelation(Guid Id, Guid Subject, Guid Object, bool Glue);

    private sealed record RegionModel(GraphRegionMap Map, Dictionary<string, List<Guid>> Members, List<RegionRelation> Relations);

    private sealed class RegionBuild(string key, string label, string kind)
    {
        public string Key { get; } = key;
        public string Label { get; set; } = label;
        public string Kind { get; set; } = kind;
        public string? Summary { get; set; }
        public string? ParentKey { get; set; }
        public HashSet<string> Sources { get; } = [];
        public int Pending { get; set; }
        public Dictionary<Guid, int> Mentions { get; } = [];
        public int Relations { get; set; }
        public string Id => RegionPaths.Id(Key);
    }

    private RegionModel Regions(SqliteConnection connection, GraphQuery query, CancellationToken cancellationToken)
    {
        long revision = Version(connection);
        string signature = Signature("knowledge-regions", null, null, query with { Limit = 40, Cursor = null });
        lock (regionGate)
        {
            if (regionCache is { } cached && cached.Revision == revision && cached.Signature == signature)
            {
                return cached.Model;
            }
        }

        RegionModel model = BuildRegions(connection, query, revision, cancellationToken);
        lock (regionGate)
        {
            regionCache = (revision, signature, model);
        }

        return model;
    }

    private static RegionModel BuildRegions(SqliteConnection connection, GraphQuery query, long revision, CancellationToken cancellationToken)
    {
        List<string> roots = ReadStrings(connection, query, """
            SELECT DISTINCT rtrim(scope, '/') FROM capture_request_context
            WHERE route IN ('watched_folder', 'selected_folder') AND scope IS NOT NULL
            """);
        Dictionary<string, (string Name, string? Purpose)> projects = [];
        using (SqliteCommand command = connection.CreateCommand("""
            SELECT rtrim(root, '/'), name, json_extract(details, '$.Purpose') FROM intake_projects
            """))
        using (SqliteDataReader reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                projects[reader.GetString(0)] = (reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
            }
        }

        // Each authorized source is placed once, under the deepest watched or selected root.
        Dictionary<string, (string Top, string? Child)> placed = [];
        Dictionary<string, RegionBuild> regions = new(StringComparer.Ordinal);
        using (SqliteCommand command = connection.CreateCommand($"""
            SELECT s.source_id, s.locator FROM sources s
            WHERE s.status = 'active' AND s.source_kind = 'file' {ScopeFilter("s")}
            """))
        {
            Scope(command, query);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string source = reader.GetString(0), locator = reader.GetString(1);
                if (RegionPaths.Place(locator, roots) is not { } placement)
                {
                    continue;
                }

                RegionBuild top = Region(regions, placement.TopKey, placement.TopLabel, placement.Loose ? "loose" : "folder");
                top.Sources.Add(source);
                if (top.Kind == "folder" && RegionPaths.TopFolder(locator, roots, placement) is { } folder
                    && projects.TryGetValue(folder, out var project))
                {
                    top.Kind = "project";
                    top.Summary = project.Purpose is { Length: > 300 } purpose ? purpose[..299] + "…" : project.Purpose;
                }

                if (placement.ChildKey is { } childKey)
                {
                    RegionBuild child = Region(regions, childKey, RegionPaths.Qualify(placement.ChildLabel!, top.Label), "folder");
                    child.ParentKey = top.Key;
                    child.Sources.Add(source);
                }

                placed[source] = (placement.TopKey, placement.ChildKey);
            }
        }

        HashSet<string> pendingSources = [];
        foreach (string source in ReadStrings(connection, query, $"""
            SELECT DISTINCT i.source_id FROM processing_jobs j
            JOIN items i ON i.item_id = j.item_id
            JOIN sources s ON s.source_id = i.source_id AND s.status = 'active'
            WHERE j.processor_key IN ('intake-triage-v1', 'intake-understand-v1') AND j.state IN ('queued', 'running')
              AND j.revision = (SELECT MAX(v.revision) FROM item_versions v WHERE v.item_id = j.item_id)
              {ScopeFilter("s")}
            """))
        {
            pendingSources.Add(source);
            if (placed.TryGetValue(source, out var at))
            {
                regions[at.Top].Pending++;
                if (at.Child is { } child)
                {
                    regions[child].Pending++;
                }
            }
        }

        // Authorized mentions place entities in every region they are known from.
        HashSet<Guid> entities = [];
        HashSet<Guid> placedEntities = [];
        Dictionary<string, Dictionary<Guid, int>> looseMentions = [];
        using (SqliteCommand command = connection.CreateCommand($"""
            SELECT m.entity_id, m.source_id, COUNT(*) FROM {MentionView(query)} m
            JOIN knowledge_entities e ON e.entity_id = m.entity_id AND e.status = 'active'
            WHERE 1 = 1 {ScopeFilter("m")}
            GROUP BY m.entity_id, m.source_id
            """))
        {
            Scope(command, query);
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                Guid entity = Guid.Parse(reader.GetString(0));
                entities.Add(entity);
                if (!placed.TryGetValue(reader.GetString(1), out var at))
                {
                    continue;
                }

                placedEntities.Add(entity);
                int count = reader.GetInt32(2);
                if (regions[at.Top].Kind == "loose")
                {
                    string source = reader.GetString(1);
                    Add(looseMentions.TryGetValue(source, out var known) ? known : looseMentions[source] = [], entity, count);
                }

                Add(regions[at.Top].Mentions, entity, count);
                if (at.Child is { } child)
                {
                    Add(regions[child].Mentions, entity, count);
                }
            }
        }

        List<RegionRelation> relations = ReadRegionRelations(connection, query, cancellationToken);
        Dictionary<Guid, int> degree = [];
        foreach (RegionRelation relation in relations)
        {
            Add(degree, relation.Subject, 1);
            Add(degree, relation.Object, 1);
        }

        HashSet<Guid> glue = [.. ReadStrings(connection, query,
            $"SELECT e.entity_id FROM knowledge_entities e WHERE e.status = 'active' AND {EntityTypeSanity.RuntimeGlueName("e")}").Select(Guid.Parse)];
        RehomeLooseFiles(connection, query, regions, looseMentions, pendingSources, glue);
        BindSuperRegions(connection, query, regions, degree);
        Dictionary<Guid, string> home = Homes(regions);

        // Cross-region evidence between top regions; internal relations count toward their region.
        Dictionary<(string, string), (int Relations, int Shared)> links = [];
        foreach (RegionRelation relation in relations.Where(relation => !relation.Glue))
        {
            if (!home.TryGetValue(relation.Subject, out string? from) || !home.TryGetValue(relation.Object, out string? to))
            {
                continue;
            }

            if (from == to)
            {
                regions[from].Relations++;
                continue;
            }

            (string a, string b) = string.CompareOrdinal(from, to) < 0 ? (from, to) : (to, from);
            links[(a, b)] = (links.GetValueOrDefault((a, b)).Relations + 1, links.GetValueOrDefault((a, b)).Shared);
        }

        // Relations among a nested region's own entities, so every level reports its connectedness.
        foreach (RegionBuild region in regions.Values.Where(region => region.ParentKey is not null))
        {
            region.Relations = relations.Count(relation => !relation.Glue
                && region.Mentions.ContainsKey(relation.Subject) && region.Mentions.ContainsKey(relation.Object));
        }

        List<RegionBuild> tops = [.. regions.Values.Where(region => region.ParentKey is null)];
        Dictionary<Guid, List<string>> topsByEntity = [];
        foreach (RegionBuild top in tops)
        {
            foreach (Guid entity in top.Mentions.Keys)
            {
                (topsByEntity.TryGetValue(entity, out List<string>? list) ? list : topsByEntity[entity] = []).Add(top.Key);
            }
        }

        foreach (List<string> shared in topsByEntity.Values.Where(list => list.Count > 1))
        {
            for (int i = 0; i < shared.Count; i++)
            {
                for (int j = i + 1; j < shared.Count; j++)
                {
                    (string a, string b) = string.CompareOrdinal(shared[i], shared[j]) < 0 ? (shared[i], shared[j]) : (shared[j], shared[i]);
                    links[(a, b)] = (links.GetValueOrDefault((a, b)).Relations, links.GetValueOrDefault((a, b)).Shared + 1);
                }
            }
        }

        // Bounded: the largest top regions, and the largest children of each.
        List<RegionBuild> keptTops = [.. tops.OrderByDescending(region => region.Mentions.Count)
            .ThenByDescending(region => region.Sources.Count).ThenBy(region => region.Key, StringComparer.Ordinal).Take(MaximumTopRegions)];
        HashSet<string> keptKeys = [.. keptTops.Select(region => region.Key)];
        List<RegionBuild> kept = [.. keptTops];
        foreach (RegionBuild top in keptTops)
        {
            kept.AddRange(Descendants(regions, top.Key));
        }

        keptKeys.UnionWith(kept.Select(region => region.Key));
        Dictionary<string, List<Guid>> members = [];
        Dictionary<string, List<Guid>> hubIds = [];
        foreach (RegionBuild region in kept)
        {
            members[region.Id] = [.. region.Mentions.OrderByDescending(entry => degree.GetValueOrDefault(entry.Key))
                .ThenByDescending(entry => entry.Value).ThenBy(entry => entry.Key).Select(entry => entry.Key)];
            // A top region's hubs are entities it owns (mentioned most there), so a project-wide name
            // such as an executable does not crown every region; deeper regions show their own best.
            int count = region.ParentKey is null ? TopHubCount : ChildHubCount;
            hubIds[region.Id] = [.. members[region.Id]
                .Where(entity => !glue.Contains(entity) && (region.ParentKey is not null || home.GetValueOrDefault(entity) == region.Key))
                .Take(count)];
        }

        Dictionary<Guid, GraphNode> hubNodes = ReadNodes(connection, [.. hubIds.Values.SelectMany(ids => ids).Distinct()], query);
        List<GraphKnowledgeRegion> result = [];
        foreach (RegionBuild region in kept)
        {
            result.Add(new GraphKnowledgeRegion(region.Id, region.ParentKey is null ? null : regions[region.ParentKey].Id,
                region.Label, region.Kind, region.Summary, region.Mentions.Count, region.Sources.Count, region.Pending,
                region.Relations, [.. hubIds[region.Id].Where(hubNodes.ContainsKey).Select(id => hubNodes[id])]));
        }

        // Every pair of kept regions where neither contains the other: confirmed relations between
        // their entities (top-level pairs also count shared entities). Views credit these to the
        // territories they draw; the strongest pairs are kept.
        Dictionary<Guid, List<string>> regionsByEntity = [];
        foreach (RegionBuild region in kept)
        {
            foreach (Guid entity in region.Mentions.Keys)
            {
                (regionsByEntity.TryGetValue(entity, out List<string>? list) ? list : regionsByEntity[entity] = []).Add(region.Key);
            }
        }

        bool Nested(string a, string b)
        {
            for (string? cursor = regions[a].ParentKey; cursor is not null; cursor = regions[cursor].ParentKey)
            {
                if (cursor == b)
                {
                    return true;
                }
            }

            for (string? cursor = regions[b].ParentKey; cursor is not null; cursor = regions[cursor].ParentKey)
            {
                if (cursor == a)
                {
                    return true;
                }
            }

            return false;
        }

        foreach (RegionRelation relation in relations.Where(relation => !relation.Glue))
        {
            if (!regionsByEntity.TryGetValue(relation.Subject, out List<string>? from) || !regionsByEntity.TryGetValue(relation.Object, out List<string>? to))
            {
                continue;
            }

            foreach (string a in from)
            {
                foreach (string b in to)
                {
                    if (a == b || (regions[a].ParentKey is null && regions[b].ParentKey is null) || Nested(a, b)
                        || regions[a].Mentions.ContainsKey(relation.Object) || regions[b].Mentions.ContainsKey(relation.Subject))
                    {
                        continue;
                    }

                    (string x, string y) = string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);
                    links[(x, y)] = (links.GetValueOrDefault((x, y)).Relations + 1, links.GetValueOrDefault((x, y)).Shared);
                }
            }
        }

        List<GraphRegionLink> regionLinks = [.. links
            .Where(entry => keptKeys.Contains(entry.Key.Item1) && keptKeys.Contains(entry.Key.Item2))
            .OrderByDescending(entry => entry.Value.Relations + entry.Value.Shared)
            .ThenBy(entry => entry.Key.Item1, StringComparer.Ordinal).ThenBy(entry => entry.Key.Item2, StringComparer.Ordinal)
            .Take(400)
            .Select(entry => new GraphRegionLink(RegionPaths.Id(entry.Key.Item1), RegionPaths.Id(entry.Key.Item2),
                entry.Value.Relations, entry.Value.Shared))];
        GraphRegionMap map = new(revision, entities.Count, entities.Count - placedEntities.Count, result, regionLinks);
        return new RegionModel(map, members, relations);
    }

    /// <summary>
    /// Evidence binding: top regions known from the same confirmed entities are one subject seen
    /// through several folders (a project's notes, client and server). Regions sharing at least
    /// three entities and a tenth of the smaller one join a group region named after the
    /// project-typed entity they share most widely, falling back to the largest member's name.
    /// </summary>
    private static void BindSuperRegions(SqliteConnection connection, GraphQuery query,
        Dictionary<string, RegionBuild> regions, Dictionary<Guid, int> degree)
    {
        List<RegionBuild> tops = [.. regions.Values.Where(region => region.ParentKey is null && region.Kind != "loose")];
        Dictionary<string, string> parent = tops.ToDictionary(region => region.Key, region => region.Key, StringComparer.Ordinal);
        string Find(string key) => parent[key] == key ? key : parent[key] = Find(parent[key]);
        for (int i = 0; i < tops.Count; i++)
        {
            for (int j = i + 1; j < tops.Count; j++)
            {
                int shared = tops[i].Mentions.Keys.Count(tops[j].Mentions.ContainsKey);
                int smaller = Math.Min(tops[i].Mentions.Count, tops[j].Mentions.Count);
                if (shared >= 3 && shared * 10 >= smaller)
                {
                    parent[Find(tops[i].Key)] = Find(tops[j].Key);
                }
            }
        }

        foreach (IGrouping<string, RegionBuild> group in tops.GroupBy(region => Find(region.Key)).Where(group => group.Count() > 1))
        {
            List<RegionBuild> members = [.. group.OrderByDescending(region => region.Mentions.Count).ThenBy(region => region.Key, StringComparer.Ordinal)];
            Dictionary<Guid, int> spread = [];
            foreach (RegionBuild member in members)
            {
                foreach (Guid entity in member.Mentions.Keys)
                {
                    Add(spread, entity, 1);
                }
            }

            List<Guid> candidates = [.. spread.Where(entry => entry.Value > 1).OrderByDescending(entry => entry.Value)
                .ThenByDescending(entry => degree.GetValueOrDefault(entry.Key)).Select(entry => entry.Key).Take(40)];
            Dictionary<Guid, GraphNode> nodes = ReadNodes(connection, candidates, query);
            // Prefer the shared project name the members' own names contain ("OpenConquer" for
            // OpenConquerClient and OpenConquerServer), then the most widely shared project.
            List<GraphNode> named = [.. candidates.Select(id => nodes.GetValueOrDefault(id))
                .OfType<GraphNode>().Where(node => node.EntityType == "project" && node.Label.Length >= 3)];
            string label = named
                .Select((node, order) => (Node: node, Order: order,
                    Contained: members.Count(member => member.Label.Contains(node.Label, StringComparison.OrdinalIgnoreCase))))
                .OrderByDescending(entry => entry.Contained).ThenBy(entry => entry.Order)
                .Select(entry => entry.Node.Label).FirstOrDefault() ?? members[0].Label;
            string key = "group:" + string.Join("|", members.Select(member => member.Key).Order(StringComparer.Ordinal));
            RegionBuild super = Region(regions, key, label, "group");
            super.Summary = "Known through " + string.Join(", ", members.Select(member => member.Label)) + ".";
            foreach (RegionBuild member in members)
            {
                member.ParentKey = key;
                super.Sources.UnionWith(member.Sources);
                super.Pending += member.Pending;
                foreach ((Guid entity, int count) in member.Mentions)
                {
                    Add(super.Mentions, entity, count);
                }
            }
        }
    }

    /// <summary>
    /// A root's loose files are not a subject. Each one joins the folder region it shares most of
    /// its knowledge with (at least three entities and 30% of its own); the rest group by shared
    /// entities into regions named after what they are about ("Neovim &amp; kitty"). Files nothing is
    /// known from yet wait in one region marked as awaiting understanding.
    /// </summary>
    private static void RehomeLooseFiles(SqliteConnection connection, GraphQuery query, Dictionary<string, RegionBuild> regions,
        Dictionary<string, Dictionary<Guid, int>> looseMentions, HashSet<string> pendingSources, HashSet<Guid> glue)
    {
        foreach (RegionBuild loose in regions.Values.Where(region => region.Kind == "loose").ToList())
        {
            regions.Remove(loose.Key);
            List<RegionBuild> folders = [.. regions.Values.Where(region => region.ParentKey is null && region.Kind != "loose")];
            List<string> remaining = [];
            foreach (string source in loose.Sources.Order(StringComparer.Ordinal))
            {
                Dictionary<Guid, int> known = looseMentions.GetValueOrDefault(source) ?? [];
                RegionBuild? best = folders
                    .Select(folder => (Folder: folder, Shared: known.Keys.Count(entity => !glue.Contains(entity) && folder.Mentions.ContainsKey(entity))))
                    .Where(entry => entry.Shared >= 3 && entry.Shared * 10 >= known.Count * 3)
                    .OrderByDescending(entry => entry.Shared).ThenBy(entry => entry.Folder.Key, StringComparer.Ordinal)
                    .Select(entry => entry.Folder).FirstOrDefault();
                if (best is null)
                {
                    remaining.Add(source);
                    continue;
                }

                best.Sources.Add(source);
                best.Pending += pendingSources.Contains(source) ? 1 : 0;
                foreach ((Guid entity, int count) in known)
                {
                    Add(best.Mentions, entity, count);
                }
            }

            Dictionary<string, string> parent = remaining.ToDictionary(source => source, source => source, StringComparer.Ordinal);
            string Find(string key) => parent[key] == key ? key : parent[key] = Find(parent[key]);
            Dictionary<Guid, string> firstSeen = [];
            foreach (string source in remaining)
            {
                foreach (Guid entity in (looseMentions.GetValueOrDefault(source) ?? []).Keys.Where(entity => !glue.Contains(entity)))
                {
                    if (firstSeen.TryGetValue(entity, out string? other))
                    {
                        parent[Find(source)] = Find(other);
                    }
                    else
                    {
                        firstSeen[entity] = source;
                    }
                }
            }

            foreach (IGrouping<string, string> component in remaining.GroupBy(Find))
            {
                Dictionary<Guid, int> mentions = [];
                foreach (string source in component)
                {
                    foreach ((Guid entity, int count) in looseMentions.GetValueOrDefault(source) ?? [])
                    {
                        Add(mentions, entity, count);
                    }
                }

                // Name a theme after what is specific to it: entities known across other regions
                // (a project's executable, say) describe the brain, not this theme.
                List<Guid> top = [.. mentions.Where(entry => !glue.Contains(entry.Key))
                    .OrderByDescending(entry => entry.Value / (1.0 + 3 * folders.Count(folder => folder.Mentions.ContainsKey(entry.Key))))
                    .ThenBy(entry => entry.Key).Select(entry => entry.Key).Take(2)];
                Dictionary<Guid, GraphNode> names = ReadNodes(connection, top, query);
                string label = top.Count == 0 ? "Awaiting understanding"
                    : string.Join(" & ", top.Where(names.ContainsKey).Select(id => names[id].Label));
                string key = loose.Key + "~" + RegionPaths.Id(string.Join("|", component.Order(StringComparer.Ordinal)));
                RegionBuild region = Region(regions, key, label.Length > 0 ? label : "Awaiting understanding", "theme");
                foreach (string source in component)
                {
                    region.Sources.Add(source);
                    region.Pending += pendingSources.Contains(source) ? 1 : 0;
                }

                foreach ((Guid entity, int count) in mentions)
                {
                    Add(region.Mentions, entity, count);
                }
            }
        }
    }

    /// <summary>For each entity, the top region it is mentioned most in.</summary>
    private static Dictionary<Guid, string> Homes(Dictionary<string, RegionBuild> regions)
    {
        Dictionary<Guid, (string Key, int Count)> best = [];
        foreach (RegionBuild region in regions.Values.Where(region => region.ParentKey is null))
        {
            foreach ((Guid entity, int count) in region.Mentions)
            {
                if (!best.TryGetValue(entity, out var current) || count > current.Count
                    || (count == current.Count && string.CompareOrdinal(region.Key, current.Key) < 0))
                {
                    best[entity] = (region.Key, count);
                }
            }
        }

        return best.ToDictionary(entry => entry.Key, entry => entry.Value.Key);
    }

    private static IEnumerable<RegionBuild> Descendants(Dictionary<string, RegionBuild> regions, string key)
    {
        foreach (RegionBuild child in regions.Values.Where(region => region.ParentKey == key)
                     .OrderByDescending(region => region.Mentions.Count).ThenBy(region => region.Key, StringComparer.Ordinal)
                     .Take(MaximumChildRegions))
        {
            yield return child;
            foreach (RegionBuild grandchild in Descendants(regions, child.Key))
            {
                yield return grandchild;
            }
        }
    }

    private static List<RegionRelation> ReadRegionRelations(SqliteConnection connection, GraphQuery query, CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand($"""
            WITH {VisibleKinds(query)}
            SELECT r.relation_id, r.subject_id, r.object_id,
                   MAX({EntityTypeSanity.RuntimeGlueName("subject")}, {EntityTypeSanity.RuntimeGlueName("object")})
            FROM knowledge_relations r
            JOIN visible_kinds subject_kind ON subject_kind.entity_id = r.subject_id
            JOIN visible_kinds object_kind ON object_kind.entity_id = r.object_id
            JOIN knowledge_entities subject ON subject.entity_id = r.subject_id
            JOIN knowledge_entities object ON object.entity_id = r.object_id
            LEFT JOIN eligible_enrichment_proposals p ON p.proposal_id = r.proposal_id
            LEFT JOIN proposal_verdicts v ON v.proposal_id = p.proposal_id
            WHERE {RelationPolicy()}
            """);
        Scope(command, query);
        command.Parameters.AddWithValue("$suggestions", query.IncludeSuggestions ? 1 : 0);
        command.Parameters.AddWithValue("$predicate", DBNull.Value);
        command.Parameters.AddWithValue("$evidenceStatus", DBNull.Value);
        using SqliteDataReader reader = command.ExecuteReader();
        List<RegionRelation> relations = [];
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            relations.Add(new RegionRelation(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)), reader.GetInt64(3) == 1));
        }

        return relations;
    }

    private static List<EdgeRow> ReadEdgesById(SqliteConnection connection, List<string> relationIds, GraphQuery query)
    {
        if (relationIds.Count == 0)
        {
            return [];
        }

        using SqliteCommand command = connection.CreateCommand($"""
            SELECT r.relation_id, r.subject_id, r.object_id, r.predicate, r.origin,
                   {RelationStatus} AS evidence_status,
                   p.proposal_id, p.source_id, p.item_id, p.revision, p.source_digest,
                   s.locator, p.evidence, p.evidence_offset, p.status, v.verdict
            FROM knowledge_relations r
            LEFT JOIN eligible_enrichment_proposals p ON p.proposal_id = r.proposal_id
            LEFT JOIN proposal_verdicts v ON v.proposal_id = p.proposal_id
            LEFT JOIN sources s ON s.source_id = p.source_id
            WHERE r.relation_id IN (SELECT value FROM json_each($ids))
            ORDER BY r.relation_id
            """);
        command.Parameters.AddWithValue("$ids", System.Text.Json.JsonSerializer.Serialize(relationIds));
        using SqliteDataReader reader = command.ExecuteReader();
        List<EdgeRow> rows = [];
        while (reader.Read())
        {
            rows.Add(ReadEdgeRow(reader));
        }

        return rows;
    }

    private static List<string> ReadStrings(SqliteConnection connection, GraphQuery query, string sql)
    {
        using SqliteCommand command = connection.CreateCommand(sql);
        if (sql.Contains("$source", StringComparison.Ordinal))
        {
            Scope(command, query);
        }

        using SqliteDataReader reader = command.ExecuteReader();
        List<string> values = [];
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private static RegionBuild Region(Dictionary<string, RegionBuild> regions, string key, string label, string kind) =>
        regions.TryGetValue(key, out RegionBuild? existing) ? existing : regions[key] = new RegionBuild(key, label, kind);

    private static void Add(Dictionary<Guid, int> counts, Guid key, int amount) =>
        counts[key] = counts.GetValueOrDefault(key) + amount;
}
