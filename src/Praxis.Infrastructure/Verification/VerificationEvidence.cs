using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Praxis.Infrastructure.Catalog;
using Praxis.Infrastructure.Knowledge;

namespace Praxis.Infrastructure.Verification;

/// <summary>
/// Builds the minimal evidence a verifier needs for each item: the proposal or entity, its
/// verbatim quote, a short surrounding context and the source file name. Nothing else from the
/// catalog is included.
/// </summary>
internal static class VerificationEvidence
{
    internal const int MaximumEntityContexts = 3;

    internal static JsonObject? ForProposal(
        SqliteConnection connection, SqliteTransaction transaction, int ordinal, string proposalId)
    {
        using var query = connection.CreateCommand("""
            SELECT p.kind, p.value, p.entity_type, p.relation_subject, p.relation_object, p.evidence,
                   p.evidence_offset, p.evidence_length, d.content, s.locator
            FROM eligible_enrichment_proposals p
            JOIN text_derivatives d ON d.item_id = p.item_id AND d.revision = p.revision
                AND d.processor_key = 'source-text-v1' AND d.source_digest = p.source_digest
            JOIN sources s ON s.source_id = p.source_id
            WHERE p.proposal_id = $proposal AND p.kind IN ('entity', 'relation')
            """);
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$proposal", proposalId);
        using var reader = query.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var item = new JsonObject { ["ordinal"] = ordinal, ["kind"] = reader.GetString(0) };
        if (reader.GetString(0) == "relation")
        {
            item["subject"] = reader.GetString(3);
            item["predicate"] = reader.GetString(1);
            item["object"] = reader.GetString(4);
        }
        else
        {
            item["name"] = reader.GetString(1);
            item["statedType"] = reader.GetString(2);
        }

        item["quote"] = reader.GetString(5);
        item["context"] = VerificationPayload.Context(reader.GetString(8), reader.GetInt32(6), reader.GetInt32(7));
        item["source"] = Path.GetFileName(reader.GetString(9));
        return item;
    }

    internal static JsonObject? ForEntityType(
        SqliteConnection connection, SqliteTransaction transaction, int ordinal, string entityId)
    {
        using var entity = connection.CreateCommand(
            "SELECT display_name FROM knowledge_entities WHERE entity_id = $entity AND status = 'active'");
        entity.Transaction = transaction;
        entity.Parameters.AddWithValue("$entity", entityId);
        if (entity.ExecuteScalar() is not string name)
        {
            return null;
        }

        using var mentions = connection.CreateCommand("""
            SELECT m.entity_type, m.evidence, p.evidence_offset, p.evidence_length, d.content, s.locator
            FROM visible_entity_mentions m
            JOIN eligible_enrichment_proposals p ON p.proposal_id = m.proposal_id
            JOIN text_derivatives d ON d.item_id = p.item_id AND d.revision = p.revision
                AND d.processor_key = 'source-text-v1' AND d.source_digest = p.source_digest
            JOIN sources s ON s.source_id = p.source_id
            WHERE m.entity_id = $entity
            ORDER BY m.entity_type, p.created_utc
            """);
        mentions.Transaction = transaction;
        mentions.Parameters.AddWithValue("$entity", entityId);
        var rows = new List<(string Type, JsonObject Context)>();
        using (var reader = mentions.ExecuteReader())
        {
            while (reader.Read() && rows.Count < 50)
            {
                rows.Add((reader.GetString(0), new JsonObject
                {
                    ["observedType"] = reader.GetString(0),
                    ["quote"] = reader.GetString(1),
                    ["context"] = VerificationPayload.Context(reader.GetString(4), reader.GetInt32(2), reader.GetInt32(3)),
                    ["source"] = Path.GetFileName(reader.GetString(5))
                }));
            }
        }

        if (rows.Count == 0)
        {
            return null;
        }

        // One context per observed type, so every disagreeing reading is represented, then more
        // contexts up to the limit.
        var selected = rows.GroupBy(row => row.Type).Select(group => group.First()).ToList();
        selected.AddRange(rows.Except(selected).Take(Math.Max(0, MaximumEntityContexts - selected.Count)));
        return new JsonObject
        {
            ["ordinal"] = ordinal,
            ["kind"] = "entity_type",
            ["name"] = name,
            ["observedTypes"] = new JsonArray(selected.Select(row => row.Type).Distinct()
                .Order(StringComparer.Ordinal).Select(type => (JsonNode)type).ToArray()),
            ["contexts"] = new JsonArray(selected.Select(row => (JsonNode)row.Context).ToArray())
        };
    }
}
