using System.Text.Json;
using Microsoft.Data.Sqlite;
using Praxis.Application.Knowledge;

namespace Praxis.Infrastructure.Persistence.Knowledge;

public sealed partial class SqliteGraphReader
{
    /// <summary>
    /// Reads many graph nodes in three queries instead of several per node. Semantics match the
    /// single-node projection: visibility and counts follow the query's mention view and scope,
    /// technical evidence types an entity unless the owner did, and the label and evidence come
    /// from the first confirmed mention by source and proposal.
    /// </summary>
    private static Dictionary<Guid, GraphNode> ReadNodes(SqliteConnection connection, IReadOnlyCollection<Guid> ids, GraphQuery query)
    {
        Dictionary<Guid, GraphNode> nodes = [];
        if (ids.Count == 0)
        {
            return nodes;
        }

        string idsJson = JsonSerializer.Serialize(ids.Select(id => id.ToString("D")));
        Dictionary<Guid, EntityRow> entities = ReadEntityRows(connection, idsJson, query);
        Dictionary<Guid, List<MentionRow>> mentions = ReadMentionRows(connection, idsJson, query);
        foreach ((Guid id, EntityRow entity) in entities)
        {
            if (entity.Status != "active" || !mentions.TryGetValue(id, out List<MentionRow>? rows) || rows.Count == 0)
            {
                continue;
            }

            nodes[id] = Compose(id, entity, rows);
        }

        return nodes;
    }

    private static GraphNode Compose(Guid id, EntityRow entity, List<MentionRow> rows)
    {
        MentionRow first = rows
            .OrderBy(row => row.Confirmed ? 0 : 1)
            .ThenBy(row => row.SourceId, StringComparer.Ordinal)
            .ThenBy(row => row.ProposalId, StringComparer.Ordinal)
            .First();
        string label = entity.Renamed ? entity.DisplayName : first.Value;

        string? type;
        string typeOrigin;
        if (entity.TypeOrigin != "owner" && entity.TechnicalType is not null)
        {
            (type, typeOrigin) = (entity.TechnicalType, "evidence");
        }
        else if (entity.TypeOrigin == "inferred")
        {
            string? frequent = rows.Where(row => row.EntityType is not null)
                .GroupBy(row => row.EntityType!, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => group.Key)
                .FirstOrDefault();
            type = frequent == "project" && entity.WorkItem ? "other" : frequent;
            typeOrigin = entity.TypeOrigin;
        }
        else
        {
            (type, typeOrigin) = (entity.StoredType, entity.TypeOrigin);
        }

        int mentionCount = rows.Select(row => row.ProposalId).Distinct(StringComparer.Ordinal).Count();
        int sources = rows.Select(row => row.SourceId).Distinct(StringComparer.Ordinal).Count();
        bool conflict = typeOrigin == "inferred" &&
                        rows.Where(row => row.EntityType is not null).Select(row => row.EntityType).Distinct(StringComparer.Ordinal).Count() > 1;
        int confirmed = entity.ConfirmedMentions;
        string status = confirmed == 0 ? "suggested" : confirmed < mentionCount ? "mixed" : "confirmed";
        GraphEvidence evidence = new(Guid.Parse(first.ProposalId), Guid.Parse(first.SourceId), Guid.Parse(first.ItemId),
            first.Revision, first.SourceDigest, first.Locator, first.Evidence, first.EvidenceOffset, first.Status, first.Verdict);
        return new GraphNode(id, label, type, typeOrigin, status, mentionCount, sources, conflict, evidence,
            ["openEntity", "openSource"]);
    }

    private static Dictionary<Guid, EntityRow> ReadEntityRows(SqliteConnection connection, string idsJson, GraphQuery query)
    {
        using SqliteCommand command = connection.CreateCommand($"""
            SELECT e.entity_id, e.display_name, e.entity_type, e.type_origin, e.status,
                   EXISTS (SELECT 1 FROM entity_decisions d WHERE d.entity_id = e.entity_id AND d.action = 'rename'),
                   {EntityTypeSanity.WorkItemName("e")},
                   {TechnicalType("e")},
                   (SELECT COUNT(DISTINCT confirmed.proposal_id) FROM confirmed_entity_mentions confirmed
                    WHERE confirmed.entity_id = e.entity_id {ScopeFilter("confirmed")})
            FROM knowledge_entities e
            WHERE e.entity_id IN (SELECT value FROM json_each($ids))
            """);
        Scope(command, query);
        command.Parameters.AddWithValue("$ids", idsJson);
        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<Guid, EntityRow> rows = [];
        while (reader.Read())
        {
            rows[Guid.Parse(reader.GetString(0))] = new EntityRow(reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetInt64(5) == 1, reader.GetInt64(6) == 1, reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.GetInt32(8));
        }

        return rows;
    }

    private static Dictionary<Guid, List<MentionRow>> ReadMentionRows(SqliteConnection connection, string idsJson, GraphQuery query)
    {
        using SqliteCommand command = connection.CreateCommand($"""
            SELECT m.entity_id, m.proposal_id, m.source_id, m.item_id, m.revision, p.source_digest, s.locator,
                   m.evidence, m.evidence_offset, m.status, m.verdict, m.entity_type,
                   CASE WHEN p.status = 'corrected' THEN p.correction ELSE p.value END,
                   EXISTS (SELECT 1 FROM confirmed_entity_mentions confirmed WHERE confirmed.proposal_id = m.proposal_id)
            FROM {MentionView(query)} m
            JOIN eligible_enrichment_proposals p ON p.proposal_id = m.proposal_id
            JOIN sources s ON s.source_id = m.source_id
            WHERE m.entity_id IN (SELECT value FROM json_each($ids)) {ScopeFilter("m")}
            """);
        Scope(command, query);
        command.Parameters.AddWithValue("$ids", idsJson);
        using SqliteDataReader reader = command.ExecuteReader();
        Dictionary<Guid, List<MentionRow>> rows = [];
        while (reader.Read())
        {
            Guid entity = Guid.Parse(reader.GetString(0));
            if (!rows.TryGetValue(entity, out List<MentionRow>? list))
            {
                rows[entity] = list = [];
            }

            list.Add(new MentionRow(reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4),
                reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetInt32(8), reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.GetString(12), reader.GetInt64(13) == 1));
        }

        return rows;
    }

    private sealed record EntityRow(string DisplayName, string? StoredType, string TypeOrigin, string Status,
        bool Renamed, bool WorkItem, string? TechnicalType, int ConfirmedMentions);

    private sealed record MentionRow(string ProposalId, string SourceId, string ItemId, int Revision, string SourceDigest,
        string Locator, string Evidence, int EvidenceOffset, string Status, string? Verdict, string? EntityType,
        string Value, bool Confirmed);
}
