using Microsoft.Data.Sqlite;
using Praxis.Domain.Knowledge;

namespace Praxis.Infrastructure.Persistence.Knowledge;

/// <summary>
/// The checks a second look runs. Each reads current, authorized evidence only (eligible proposals
/// of active sources) and explains itself in the revision basis.
/// </summary>
internal static class ReconsolidationRules
{
    // Relations that count as knowledge: owner-made, reviewed, verified or documented by a strong tier.
    private static readonly string ConfirmedRelations = $"""
        SELECT r.subject_id, r.predicate, r.object_id
        FROM knowledge_relations r
        JOIN knowledge_entities subject ON subject.entity_id = r.subject_id AND subject.status = 'active'
        JOIN knowledge_entities object ON object.entity_id = r.object_id AND object.status = 'active'
        LEFT JOIN eligible_enrichment_proposals p ON p.proposal_id = r.proposal_id
        LEFT JOIN proposal_verdicts v ON v.proposal_id = p.proposal_id
        WHERE r.origin = 'owner'
           OR (p.proposal_id IS NOT NULL AND {SqliteGraphReader.RelationStatus} NOT IN ('suggested', 'contradicted'))
        """;

    private static readonly IReadOnlySet<string> CodeTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "function", "class", "binary_module"
    };

    /// <summary>
    /// Brings a stale inferred type back in line with the evidence that remains: the most common
    /// type among visible mentions, with the work-item correction. Returns true when it changed.
    /// </summary>
    internal static bool RefreshInferredType(SqliteConnection connection, SqliteTransaction transaction,
        string entity, string? stored, string trigger, string now)
    {
        using SqliteCommand query = connection.CreateCommand($"""
            SELECT ranked.entity_type, {EntityTypeSanity.CorrectInferred("ranked.entity_type", "e")}, ranked.count,
                   (SELECT COUNT(*) FROM visible_entity_mentions all_m WHERE all_m.entity_id = e.entity_id AND all_m.entity_type IS NOT NULL)
            FROM knowledge_entities e
            JOIN (SELECT m.entity_type, COUNT(*) AS count FROM visible_entity_mentions m
                  WHERE m.entity_id = $entity AND m.entity_type IS NOT NULL
                  GROUP BY m.entity_type ORDER BY COUNT(*) DESC, m.entity_type LIMIT 1) ranked
            WHERE e.entity_id = $entity
            """);
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$entity", entity);
        using SqliteDataReader reader = query.ExecuteReader();
        if (!reader.Read())
        {
            return false;
        }

        string majority = reader.GetString(0), corrected = reader.GetString(1);
        long count = reader.GetInt64(2), total = reader.GetInt64(3);
        reader.Close();
        if (corrected == stored)
        {
            return false;
        }

        connection.Run(transaction, """
            UPDATE knowledge_entities SET entity_type = $type, updated_utc = $now
            WHERE entity_id = $entity AND type_origin = 'inferred'
            """, ("$type", corrected), ("$now", now), ("$entity", entity));
        string reason = majority != corrected
            ? "Its name is a ticket or pull-request reference: tracked work, not a project."
            : $"{count} of {total} current mentions call it {corrected}.";
        KnowledgeRevisionLog.Applied(connection, transaction, entity, stored, corrected, trigger, Prefix(trigger) + reason, now);
        return true;
    }

    /// <summary>
    /// Proposes a code type when confirmed relations only code can have contradict a non-code type:
    /// an entity that inherits is a class; one that takes part in several calls is a function.
    /// Skipped when deterministic code evidence already types the entity.
    /// </summary>
    internal static bool RelationSignature(SqliteConnection connection, SqliteTransaction transaction,
        string entity, string? type, string now)
    {
        if (type is null || CodeTypes.Contains(type) || HasTechnicalType(connection, transaction, entity))
        {
            return false;
        }

        using SqliteCommand query = connection.CreateCommand($"""
            WITH confirmed AS ({ConfirmedRelations})
            SELECT SUM(predicate = 'inherits_from'), SUM(predicate = 'calls')
            FROM confirmed WHERE subject_id = $entity OR object_id = $entity
            """);
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$entity", entity);
        using SqliteDataReader reader = query.ExecuteReader();
        reader.Read();
        long inherits = reader.IsDBNull(0) ? 0 : reader.GetInt64(0);
        long calls = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
        reader.Close();
        if (inherits > 0)
        {
            return KnowledgeRevisionLog.Suggested(connection, transaction, entity, type, "class", RevisionVocabulary.Relations,
                $"It takes part in {inherits} confirmed inherits_from relation(s); only classes inherit.", now);
        }

        return calls >= 2 && KnowledgeRevisionLog.Suggested(connection, transaction, entity, type, "function", RevisionVocabulary.Relations,
            $"It takes part in {calls} confirmed calls relations; only code calls code.", now);
    }

    /// <summary>
    /// After the owner corrects <paramref name="entity"/> from <paramref name="previous"/>, proposes
    /// the same correction for inferred entities that still carry <paramref name="previous"/> and
    /// relate to a shared neighbor the same way the corrected entity does. Returns proposals made.
    /// </summary>
    internal static int OwnerLessons(SqliteConnection connection, SqliteTransaction transaction,
        string entity, string corrected, string previous, string now)
    {
        List<(string Candidate, string Via, string Predicate, long Shared)> candidates = [];
        using (SqliteCommand query = connection.CreateCommand($"""
            WITH confirmed AS MATERIALIZED ({ConfirmedRelations}),
            roles AS (
                SELECT object_id AS neighbor, predicate, 'out' AS direction FROM confirmed WHERE subject_id = $entity
                UNION
                SELECT subject_id, predicate, 'in' FROM confirmed WHERE object_id = $entity
            ),
            matches AS (
                SELECT CASE roles.direction WHEN 'out' THEN c.subject_id ELSE c.object_id END AS candidate,
                       roles.neighbor, roles.predicate
                FROM roles
                JOIN confirmed c ON c.predicate = roles.predicate
                 AND ((roles.direction = 'out' AND c.object_id = roles.neighbor)
                   OR (roles.direction = 'in' AND c.subject_id = roles.neighbor))
            )
            SELECT matches.candidate, MIN(n.display_name), MIN(matches.predicate), COUNT(DISTINCT matches.neighbor || matches.predicate) AS shared
            FROM matches
            JOIN knowledge_entities l ON l.entity_id = matches.candidate
            JOIN knowledge_entities n ON n.entity_id = matches.neighbor
            WHERE matches.candidate <> $entity AND l.status = 'active' AND l.type_origin = 'inferred'
              AND l.entity_type = $previous
              AND NOT EXISTS (SELECT 1 FROM technical_entity_types t WHERE t.entity_id = l.entity_id)
            GROUP BY matches.candidate
            ORDER BY shared DESC, matches.candidate
            LIMIT 25
            """))
        {
            query.Transaction = transaction;
            query.Parameters.AddWithValue("$entity", entity);
            query.Parameters.AddWithValue("$previous", previous);
            using SqliteDataReader reader = query.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
            }
        }

        string name = Name(connection, transaction, entity);
        int proposed = 0;
        foreach ((string candidate, string via, string predicate, long shared) in candidates)
        {
            string basis = $"You changed {name} from {previous} to {corrected}. This is also {previous} and {predicate} {via} the same way"
                           + (shared > 1 ? $" ({shared} shared roles)." : ".");
            if (KnowledgeRevisionLog.Suggested(connection, transaction, candidate, previous, corrected, RevisionVocabulary.OwnerLesson, basis, now))
            {
                proposed++;
            }
        }

        return proposed;
    }

    private static bool HasTechnicalType(SqliteConnection connection, SqliteTransaction transaction, string entity)
    {
        using SqliteCommand query = connection.CreateCommand("SELECT 1 FROM technical_entity_types WHERE entity_id = $entity");
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$entity", entity);
        return query.ExecuteScalar() is not null;
    }

    private static string Name(SqliteConnection connection, SqliteTransaction transaction, string entity)
    {
        using SqliteCommand query = connection.CreateCommand("SELECT display_name FROM knowledge_entities WHERE entity_id = $entity");
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$entity", entity);
        return query.ExecuteScalar() as string ?? "an entity";
    }

    private static string Prefix(string trigger) => trigger switch
    {
        RevisionVocabulary.Rules => "Classification rules changed. ",
        RevisionVocabulary.Revocation => "A source it was known from was revoked. ",
        RevisionVocabulary.Mentions => "New evidence arrived. ",
        _ => ""
    };
}
