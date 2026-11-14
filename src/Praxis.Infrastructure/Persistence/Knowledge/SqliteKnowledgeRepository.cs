using Microsoft.Data.Sqlite;
using Praxis.Application.Knowledge;
using Praxis.Application.Understanding;
using Praxis.Domain.Enrichment;
using Praxis.Domain.Knowledge;

namespace Praxis.Infrastructure.Persistence.Knowledge;

/// <summary>
/// Canonical entities across all sources: what is known about a name, where it is mentioned,
/// how it relates to other entities, and the owner's governing decisions (rename, type, merge,
/// reject, restore, relate). Everything shown is derived from currently visible evidence, so a
/// revoked source or superseded revision drops out without deleting owner decisions.
/// </summary>
public sealed class SqliteKnowledgeRepository : IKnowledgeRepository
{
    private const string Summary = """
        SELECT e.entity_id, e.display_name,
               CASE WHEN e.type_origin = 'owner' THEN e.entity_type ELSE COALESCE(t.entity_type, e.entity_type) END,
               CASE WHEN e.type_origin <> 'owner' AND t.entity_type IS NOT NULL THEN 'evidence' ELSE e.type_origin END, e.status,
               COUNT(m.proposal_id), COUNT(DISTINCT m.source_id),
               e.type_origin = 'inferred' AND t.entity_type IS NULL AND COUNT(DISTINCT m.entity_type) > 1
        FROM knowledge_entities e
        LEFT JOIN technical_entity_types t ON t.entity_id = e.entity_id
        LEFT JOIN visible_entity_mentions m ON m.entity_id = e.entity_id
        """ + "\n";

    // Knowledge is what the owner confirmed, a verifier supported, or the strong tiers verified;
    // local-model guesses are suggestions until then.
    private const string ConfirmedSummary = """
        SELECT e.entity_id, e.display_name,
               CASE WHEN e.type_origin = 'owner' THEN e.entity_type ELSE COALESCE(t.entity_type, e.entity_type) END,
               CASE WHEN e.type_origin <> 'owner' AND t.entity_type IS NOT NULL THEN 'evidence' ELSE e.type_origin END, e.status,
               COUNT(m.proposal_id), COUNT(DISTINCT m.source_id),
               e.type_origin = 'inferred' AND t.entity_type IS NULL AND COUNT(DISTINCT m.entity_type) > 1
        FROM knowledge_entities e
        LEFT JOIN technical_entity_types t ON t.entity_id = e.entity_id
        LEFT JOIN confirmed_entity_mentions m ON m.entity_id = e.entity_id
        """ + "\n";

    private readonly CatalogDatabase database;

    private readonly TimeProvider clock;

    internal SqliteKnowledgeRepository(CatalogDatabase database, TimeProvider clock)
    {
        this.database = database;
        this.clock = clock;
    }

    /// <summary>
    /// Active entities with confirmed evidence (or, with <paramref name="includeSuggestions"/>, any
    /// visible evidence) or an owner decision, most mentioned first.
    /// </summary>
    public IReadOnlyList<EntitySummary> ListEntities(string? entityType = null, string? search = null, int limit = 100, bool includeSuggestions = false)
    {
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (entityType is not null && !KnowledgeVocabulary.EntityTypes.Contains(entityType))
        {
            throw new ArgumentException("The entity type is not supported.", nameof(entityType));
        }

        string? key = search is null ? null : KnowledgeVocabulary.NameKey(search);
        return database.ExecuteRead(connection =>
        {
            using SqliteCommand query = connection.CreateCommand((includeSuggestions ? Summary : ConfirmedSummary) + """
                                                                           WHERE e.status = 'active'
                                                                             AND ($type IS NULL OR (CASE WHEN e.type_origin = 'owner' THEN e.entity_type ELSE COALESCE(t.entity_type, e.entity_type) END) = $type)
                                                                             AND ($search IS NULL OR EXISTS (SELECT 1 FROM entity_aliases a
                                                                                  WHERE a.entity_id = e.entity_id AND instr(a.alias_key, $search) > 0))
                                                                           GROUP BY e.entity_id
                                                                           HAVING COUNT(m.proposal_id) > 0 OR e.type_origin = 'owner'
                                                                           ORDER BY COUNT(m.proposal_id) DESC, e.display_name, e.entity_id
                                                                           LIMIT $limit
                                                                           """);
            query.Parameters.AddWithValue("$type", (object?)entityType ?? DBNull.Value);
            query.Parameters.AddWithValue("$search", (object?)key ?? DBNull.Value);
            query.Parameters.AddWithValue("$limit", limit);
            return ReadSummaries(query);
        });
    }

    public EntityDetail? GetEntity(Guid entityId)
    {
        RequireId(entityId);
        return database.ExecuteRead(connection =>
        {
            using SqliteCommand summary = connection.CreateCommand(Summary + """
                                                                             WHERE e.entity_id = $entity GROUP BY e.entity_id
                                                                             """);
            summary.Parameters.AddWithValue("$entity", entityId.ToString("D"));
            if (ReadSummaries(summary) is not [var found])
            {
                return null;
            }

            return new EntityDetail(found, Aliases(connection, entityId), Mentions(connection, entityId),
                Relations(connection, entityId), Related(connection, entityId),
                TechnicalEvidence.IsClassName(found.Name) ? Members(connection, entityId, found.Name) : [],
                TechnicalEvidence.OwningClass(found.Name) is { } owner ? Owner(connection, owner) : null);
        });
    }

    public bool Rename(Guid entityId, string name)
    {
        string key = KnowledgeVocabulary.NameKey(name)
                     ?? throw new ArgumentException("The entity name is invalid.", nameof(name));
        string display = name.Trim();
        return Decide(entityId, "rename", display, (connection, transaction, now) =>
        {
            using SqliteCommand owner = connection.CreateCommand("SELECT entity_id FROM entity_aliases WHERE alias_key = $key");
            owner.Transaction = transaction;
            owner.Parameters.AddWithValue("$key", key);
            if (owner.ExecuteScalar() is string other && other != entityId.ToString("D"))
            {
                throw new InvalidOperationException("Another entity already uses this name; merge them instead.");
            }

            connection.Run(transaction, """
                UPDATE knowledge_entities SET display_name = $name, updated_utc = $now WHERE entity_id = $entity;
                INSERT INTO entity_aliases (alias_key, entity_id, display, origin, created_utc)
                VALUES ($key, $entity, $name, 'owner', $now)
                ON CONFLICT(alias_key) DO UPDATE SET display = excluded.display, origin = 'owner';
                """, ("$name", display), ("$now", now), ("$entity", entityId.ToString("D")), ("$key", key));
        });
    }

    public bool SetType(Guid entityId, string entityType)
    {
        if (!KnowledgeVocabulary.EntityTypes.Contains(entityType))
        {
            throw new ArgumentException("The entity type is not supported.", nameof(entityType));
        }

        return Decide(entityId, "set_type", entityType, (connection, transaction, now) =>
            connection.Run(transaction, """
                UPDATE knowledge_entities SET entity_type = $type, type_origin = 'owner', updated_utc = $now
                WHERE entity_id = $entity
                """, ("$type", entityType), ("$now", now), ("$entity", entityId.ToString("D"))));
    }

    /// <summary>
    /// Folds <paramref name="sourceEntityId"/> into <paramref name="targetEntityId"/>: aliases,
    /// mentions and relations move; relations that would point an entity at itself are dropped;
    /// the source entity remains as a merged record for audit.
    /// </summary>
    public bool Merge(Guid sourceEntityId, Guid targetEntityId)
    {
        RequireId(targetEntityId);
        if (sourceEntityId == targetEntityId)
        {
            throw new ArgumentException("An entity cannot be merged into itself.", nameof(targetEntityId));
        }

        string source = sourceEntityId.ToString("D");
        string target = targetEntityId.ToString("D");
        return Decide(sourceEntityId, "merge", "into " + target, (connection, transaction, now) =>
        {
            if (!IsActive(connection, transaction, target))
            {
                throw new InvalidOperationException("The merge target is not an active entity.");
            }

            connection.Run(transaction, """
                UPDATE entity_aliases SET entity_id = $target WHERE entity_id = $source;
                UPDATE entity_mentions SET entity_id = $target WHERE entity_id = $source;
                DELETE FROM knowledge_relations
                WHERE (subject_id = $source AND object_id = $target)
                   OR (subject_id = $target AND object_id = $source);
                UPDATE knowledge_relations SET subject_id = $target WHERE subject_id = $source;
                UPDATE knowledge_relations SET object_id = $target WHERE object_id = $source;
                UPDATE knowledge_entities SET status = 'merged', merged_into = $target, updated_utc = $now
                WHERE entity_id = $source;
                """, ("$source", source), ("$target", target), ("$now", now));
            KnowledgeLinker.RecomputeInferredTypes(connection, transaction, [target], now);
        });
    }

    /// <summary>
    /// Marks a name as not a valid entity. It disappears from the graph, its aliases keep
    /// resolving to it (so future mentions stay hidden), and the local model is told not to
    /// report it again.
    /// </summary>
    public bool Reject(Guid entityId) => SetStatus(entityId, "rejected", "reject");

    public bool Restore(Guid entityId) => SetStatus(entityId, "active", "restore");

    public Guid Relate(Guid subjectEntityId, string predicate, Guid objectEntityId)
    {
        RequireId(objectEntityId);
        if (!KnowledgeVocabulary.Predicates.Contains(predicate))
        {
            throw new ArgumentException("The relation predicate is not supported.", nameof(predicate));
        }

        if (subjectEntityId == objectEntityId)
        {
            throw new ArgumentException("A relation needs two different entities.", nameof(objectEntityId));
        }

        Guid relation = Guid.NewGuid();
        bool recorded = Decide(subjectEntityId, "relate", $"{predicate} {objectEntityId:D}", (connection, transaction, now) =>
        {
            if (!IsActive(connection, transaction, objectEntityId.ToString("D")))
            {
                throw new InvalidOperationException("The related entity is not active.");
            }

            connection.Run(transaction, """
                INSERT INTO knowledge_relations
                    (relation_id, subject_id, predicate, object_id, origin, proposal_id, created_utc)
                VALUES ($id, $subject, $predicate, $object, 'owner', NULL, $now)
                """, ("$id", relation.ToString("D")), ("$subject", subjectEntityId.ToString("D")),
                ("$predicate", predicate), ("$object", objectEntityId.ToString("D")), ("$now", now));
        });
        return recorded ? relation : throw new InvalidOperationException("The subject entity is not available.");
    }

    /// <summary>Owner and verifier decisions the local model should respect, for prompt hints.</summary>
    public IReadOnlyList<EntityHint> GoverningHints(int limit = 2_000) => database.Execute(connection =>
    {
        using SqliteCommand query = connection.CreateCommand("""
                                                             SELECT a.display, e.entity_type, e.status = 'rejected'
                                                             FROM entity_aliases a JOIN knowledge_entities e ON e.entity_id = a.entity_id
                                                             WHERE (e.status = 'rejected' OR (e.status = 'active' AND e.type_origin IN ('owner', 'verified')))
                                                             ORDER BY a.alias_key LIMIT $limit
                                                             """);
        query.Parameters.AddWithValue("$limit", limit);
        using SqliteDataReader reader = query.ExecuteReader();
        List<EntityHint> hints = new List<EntityHint>();
        while (reader.Read())
        {
            hints.Add(new EntityHint(reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetInt64(2) == 1));
        }

        return hints;
    });

    private bool SetStatus(Guid entityId, string status, string action) =>
        Decide(entityId, action, status, (connection, transaction, now) =>
            connection.Run(transaction, """
                UPDATE knowledge_entities SET status = $status, updated_utc = $now
                WHERE entity_id = $entity AND status <> 'merged'
                """, ("$status", status), ("$now", now), ("$entity", entityId.ToString("D"))));

    /// <summary>Applies an owner decision to an existing, unmerged entity and records it.</summary>
    private bool Decide(
        Guid entityId, string action, string detail, Action<SqliteConnection, SqliteTransaction, string> apply)
    {
        RequireId(entityId);
        string now = CatalogValues.Timestamp(clock.GetUtcNow());
        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand exists = connection.CreateCommand(
                "SELECT status FROM knowledge_entities WHERE entity_id = $entity");
            exists.Transaction = transaction;
            exists.Parameters.AddWithValue("$entity", entityId.ToString("D"));
            if (exists.ExecuteScalar() is not string status || status == "merged")
            {
                return false;
            }

            apply(connection, transaction, now);
            connection.Run(transaction, """
                INSERT INTO entity_decisions (decision_id, entity_id, action, detail, decided_utc)
                VALUES ($id, $entity, $action, $detail, $now)
                """, ("$id", Guid.NewGuid().ToString("D")), ("$entity", entityId.ToString("D")),
                ("$action", action), ("$detail", detail), ("$now", now));
            transaction.Commit();
            return true;
        });
    }

    private static bool IsActive(SqliteConnection connection, SqliteTransaction transaction, string entity)
    {
        using SqliteCommand query = connection.CreateCommand(
            "SELECT 1 FROM knowledge_entities WHERE entity_id = $entity AND status = 'active'");
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$entity", entity);
        return query.ExecuteScalar() is not null;
    }

    private static List<EntitySummary> ReadSummaries(SqliteCommand query)
    {
        using SqliteDataReader reader = query.ExecuteReader();
        List<EntitySummary> result = new List<EntitySummary>();
        while (reader.Read())
        {
            result.Add(new EntitySummary(Guid.Parse(reader.GetString(0)), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetInt32(5), reader.GetInt32(6), reader.GetInt64(7) == 1));
        }

        return result;
    }

    private static List<string> Aliases(SqliteConnection connection, Guid entityId)
    {
        using SqliteCommand query = connection.CreateCommand(
            "SELECT display FROM entity_aliases WHERE entity_id = $entity ORDER BY alias_key");
        query.Parameters.AddWithValue("$entity", entityId.ToString("D"));
        using SqliteDataReader reader = query.ExecuteReader();
        List<string> aliases = new List<string>();
        while (reader.Read())
        {
            aliases.Add(reader.GetString(0));
        }

        return aliases;
    }

    private static List<EntityMention> Mentions(SqliteConnection connection, Guid entityId)
    {
        using SqliteCommand query = connection.CreateCommand("""
                                                             SELECT m.proposal_id, m.source_id, m.item_id, m.revision, m.entity_type, m.evidence,
                                                                    m.evidence_offset,
                                                                    CASE WHEN m.processor_key = 'technical-evidence-v1' AND c.proposal_id IS NOT NULL THEN 'documented'
                                                                         WHEN m.status = 'pending' AND c.proposal_id IS NOT NULL THEN 'verified' ELSE m.status END,
                                                                    s.locator, m.verdict
                                                             FROM visible_entity_mentions m JOIN sources s ON s.source_id = m.source_id
                                                             LEFT JOIN confirmed_entity_mentions c ON c.proposal_id = m.proposal_id
                                                             WHERE m.entity_id = $entity
                                                             ORDER BY s.locator, m.evidence_offset LIMIT 200
                                                             """);
        query.Parameters.AddWithValue("$entity", entityId.ToString("D"));
        using SqliteDataReader reader = query.ExecuteReader();
        List<EntityMention> mentions = new List<EntityMention>();
        while (reader.Read())
        {
            mentions.Add(new EntityMention(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)), reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetString(5),
                reader.GetInt32(6), reader.GetString(7), Path.GetFileName(reader.GetString(8)),
                reader.IsDBNull(9) ? null : reader.GetString(9)));
        }

        return mentions;
    }

    /// <summary>
    /// Owner relations, and model relations whose proposal is visible under the same rule as
    /// mentions: owner decisions first, then verifier verdicts, then the instruction flag.
    /// </summary>
    private static List<EntityRelation> Relations(SqliteConnection connection, Guid entityId)
    {
        using SqliteCommand query = connection.CreateCommand("""
                                                             SELECT r.relation_id, r.predicate, r.subject_id = $entity,
                                                                    CASE WHEN r.subject_id = $entity THEN r.object_id ELSE r.subject_id END,
                                                                    other.display_name, CASE WHEN p.processor_key = 'technical-evidence-v1' THEN 'evidence' ELSE r.origin END, p.evidence
                                                             FROM knowledge_relations r
                                                             JOIN knowledge_entities other ON other.entity_id =
                                                                 CASE WHEN r.subject_id = $entity THEN r.object_id ELSE r.subject_id END
                                                             LEFT JOIN eligible_enrichment_proposals p ON p.proposal_id = r.proposal_id
                                                             LEFT JOIN proposal_verdicts v ON v.proposal_id = p.proposal_id
                                                             WHERE (r.subject_id = $entity OR r.object_id = $entity) AND other.status = 'active'
                                                               AND (r.origin = 'owner' OR (p.proposal_id IS NOT NULL AND p.status <> 'rejected'
                                                                    AND (p.status = 'accepted' OR (COALESCE(v.verdict, '') <> 'unsupported'
                                                                         AND (p.flags IS NULL OR v.verdict = 'supported')))))
                                                             ORDER BY r.predicate, other.display_name LIMIT 200
                                                             """);
        query.Parameters.AddWithValue("$entity", entityId.ToString("D"));
        using SqliteDataReader reader = query.ExecuteReader();
        List<EntityRelation> relations = new List<EntityRelation>();
        while (reader.Read())
        {
            relations.Add(new EntityRelation(Guid.Parse(reader.GetString(0)), reader.GetString(1),
                reader.GetInt64(2) == 1, Guid.Parse(reader.GetString(3)), reader.GetString(4),
                reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return relations;
    }

    /// <summary>Entities mentioned in the same segments, strongest co-occurrence first.</summary>
    private static List<RelatedEntity> Related(SqliteConnection connection, Guid entityId)
    {
        using SqliteCommand query = connection.CreateCommand("""
                                                             SELECT other.entity_id, other.display_name, other.entity_type,
                                                                    COUNT(DISTINCT g.item_id || ':' || g.revision || ':' || g.ordinal)
                                                             FROM visible_entity_mentions self
                                                             JOIN text_segments g ON g.item_id = self.item_id AND g.revision = self.revision
                                                                 AND self.evidence_offset >= g.start_offset
                                                                 AND self.evidence_offset < g.start_offset + g.length
                                                             JOIN visible_entity_mentions m ON m.item_id = g.item_id AND m.revision = g.revision
                                                                 AND m.evidence_offset >= g.start_offset
                                                                 AND m.evidence_offset < g.start_offset + g.length
                                                                 AND m.entity_id <> self.entity_id
                                                             JOIN knowledge_entities other ON other.entity_id = m.entity_id AND other.status = 'active'
                                                             WHERE self.entity_id = $entity
                                                             GROUP BY other.entity_id
                                                             ORDER BY 4 DESC, other.display_name LIMIT 20
                                                             """);
        query.Parameters.AddWithValue("$entity", entityId.ToString("D"));
        using SqliteDataReader reader = query.ExecuteReader();
        List<RelatedEntity> related = new List<RelatedEntity>();
        while (reader.Read())
        {
            related.Add(new RelatedEntity(Guid.Parse(reader.GetString(0)), reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetInt32(3)));
        }

        return related;
    }

    /// <summary>Visible entities named as members of a class (<c>CRadar_*</c> for <c>CRadar</c>).</summary>
    private static List<RelatedEntity> Members(SqliteConnection connection, Guid entityId, string className)
    {
        // GLOB is case-sensitive and treats '_' literally; class names are letters and digits only.
        using SqliteCommand query = connection.CreateCommand("""
            SELECT e.entity_id, e.display_name, e.entity_type
            FROM knowledge_entities e
            WHERE e.status = 'active' AND e.entity_id <> $entity AND e.display_name GLOB $pattern
              AND EXISTS (SELECT 1 FROM visible_entity_mentions m WHERE m.entity_id = e.entity_id)
            ORDER BY e.display_name LIMIT 200
            """);
        query.Parameters.AddWithValue("$entity", entityId.ToString("D"));
        query.Parameters.AddWithValue("$pattern", className + "_?*");
        return ReadNamed(query);
    }

    /// <summary>The visible class entity a member name belongs to, if Praxis knows it.</summary>
    private static RelatedEntity? Owner(SqliteConnection connection, string className)
    {
        using SqliteCommand query = connection.CreateCommand("""
            SELECT e.entity_id, e.display_name, e.entity_type
            FROM knowledge_entities e
            WHERE e.status = 'active' AND e.display_name = $name
              AND EXISTS (SELECT 1 FROM visible_entity_mentions m WHERE m.entity_id = e.entity_id)
            ORDER BY e.entity_id LIMIT 1
            """);
        query.Parameters.AddWithValue("$name", className);
        return ReadNamed(query).FirstOrDefault();
    }

    private static List<RelatedEntity> ReadNamed(SqliteCommand query)
    {
        using SqliteDataReader reader = query.ExecuteReader();
        List<RelatedEntity> entities = [];
        while (reader.Read())
        {
            entities.Add(new RelatedEntity(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), 0));
        }

        return entities;
    }

    private static void RequireId(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An entity identifier is required.", nameof(id));
        }
    }
}
