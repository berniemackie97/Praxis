using Microsoft.Data.Sqlite;
using Praxis.Storage.Catalog;
using Praxis.Storage.Enrichment;

namespace Praxis.Storage.Knowledge;

/// <summary>
/// Links proposals to canonical entities inside the transaction that records them. A name
/// resolves through its normalized alias key; unknown names create an entity whose type is
/// inferred from its visible mentions until a verifier or the owner sets it. Relations connect
/// the entities resolved for their subject and object.
/// </summary>
internal static class KnowledgeLinker
{
    internal static void Link(
        SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<(string ProposalId, ProposalCandidate Candidate)> proposals, string now)
    {
        var affected = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (proposalId, candidate) in proposals.Where(entry => entry.Candidate.Kind == "entity"))
        {
            if (ResolveOrCreate(connection, transaction, candidate.Value, candidate.EntityType, now) is { } entity)
            {
                Execute(connection, transaction, """
                    INSERT INTO entity_mentions (proposal_id, entity_id, created_utc)
                    VALUES ($proposal, $entity, $now)
                    """, ("$proposal", proposalId), ("$entity", entity), ("$now", now));
                affected.Add(entity);
            }
        }

        foreach (var (proposalId, candidate) in proposals.Where(entry => entry.Candidate.Kind == "relation"))
        {
            var subject = ResolveOrCreate(connection, transaction, candidate.RelationSubject!, null, now);
            var obj = ResolveOrCreate(connection, transaction, candidate.RelationObject!, null, now);
            if (subject is not null && obj is not null && subject != obj)
            {
                Execute(connection, transaction, """
                    INSERT INTO knowledge_relations
                        (relation_id, subject_id, predicate, object_id, origin, proposal_id, created_utc)
                    VALUES ($id, $subject, $predicate, $object, 'model', $proposal, $now)
                    """, ("$id", Guid.NewGuid().ToString("D")), ("$subject", subject),
                    ("$predicate", candidate.Value), ("$object", obj), ("$proposal", proposalId), ("$now", now));
            }
        }

        RecomputeInferredTypes(connection, transaction, affected, now);
    }

    /// <summary>Moves an entity mention to the entity named by an owner correction.</summary>
    internal static void Relink(
        SqliteConnection connection, SqliteTransaction transaction, string proposalId, string name, string now)
    {
        using var current = connection.CreateCommand(
            "SELECT entity_id FROM entity_mentions WHERE proposal_id = $proposal");
        current.Transaction = transaction;
        current.Parameters.AddWithValue("$proposal", proposalId);
        if (current.ExecuteScalar() is not string previous ||
            ResolveOrCreate(connection, transaction, name, null, now) is not { } next || next == previous)
        {
            return;
        }

        Execute(connection, transaction, "UPDATE entity_mentions SET entity_id = $entity WHERE proposal_id = $proposal",
            ("$entity", next), ("$proposal", proposalId));
        RecomputeInferredTypes(connection, transaction, [previous, next], now);
    }

    /// <summary>Entity for a name, creating it (with a mention-origin alias) when unknown.</summary>
    internal static string? ResolveOrCreate(
        SqliteConnection connection, SqliteTransaction transaction, string name, string? type, string now)
    {
        if (KnowledgeVocabulary.NameKey(name) is not { } key)
        {
            return null;
        }

        using var lookup = connection.CreateCommand("SELECT entity_id FROM entity_aliases WHERE alias_key = $key");
        lookup.Transaction = transaction;
        lookup.Parameters.AddWithValue("$key", key);
        if (lookup.ExecuteScalar() is string existing)
        {
            return existing;
        }

        var entity = Guid.NewGuid().ToString("D");
        Execute(connection, transaction, """
            INSERT INTO knowledge_entities
                (entity_id, display_name, entity_type, type_origin, status, merged_into, created_utc, updated_utc)
            VALUES ($entity, $name, $type, 'inferred', 'active', NULL, $now, $now);
            INSERT INTO entity_aliases (alias_key, entity_id, display, origin, created_utc)
            VALUES ($key, $entity, $name, 'mention', $now);
            """, ("$entity", entity), ("$name", name.Trim()), ("$type", type), ("$key", key), ("$now", now));
        return entity;
    }

    /// <summary>
    /// For entities whose type is still inferred, adopts the most frequent type among visible
    /// mentions (ties broken by name for determinism). Verified and owner types are never changed.
    /// </summary>
    internal static void RecomputeInferredTypes(
        SqliteConnection connection, SqliteTransaction transaction, IEnumerable<string> entities, string now)
    {
        foreach (var entity in entities.Distinct(StringComparer.Ordinal))
        {
            Execute(connection, transaction, """
                UPDATE knowledge_entities
                SET entity_type = COALESCE((
                        SELECT m.entity_type FROM visible_entity_mentions m
                        WHERE m.entity_id = $entity
                        GROUP BY m.entity_type ORDER BY COUNT(*) DESC, m.entity_type LIMIT 1),
                        entity_type),
                    updated_utc = $now
                WHERE entity_id = $entity AND type_origin = 'inferred'
                """, ("$entity", entity), ("$now", now));
        }
    }

    internal static void Execute(
        SqliteConnection connection, SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand(sql);
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        command.ExecuteNonQuery();
    }
}
