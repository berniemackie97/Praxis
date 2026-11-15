using System.Globalization;
using Microsoft.Data.Sqlite;
using Praxis.Application.Knowledge;
using Praxis.Domain.Knowledge;

namespace Praxis.Infrastructure.Persistence.Knowledge;

/// <summary>
/// Works through entities whose evidence changed: refreshes stale inferred types (applied), and
/// proposes corrections that relations or owner lessons imply (suggested). Owner and verified
/// types are never revised; their owner corrections only teach.
/// </summary>
public sealed class SqliteKnowledgeReconsolidation : IKnowledgeReconsolidation
{
    private const string RulesVersionKey = "knowledge_rules_version";
    private readonly CatalogDatabase database;
    private readonly TimeProvider clock;

    internal SqliteKnowledgeReconsolidation(CatalogDatabase database, TimeProvider clock)
    {
        this.database = database;
        this.clock = clock;
    }

    public int EnqueueForRuleChange() => database.Execute(connection =>
    {
        string now = CatalogValues.Timestamp(clock.GetUtcNow());
        string version = EntityTypeSanity.Version.ToString(CultureInfo.InvariantCulture);
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand current = connection.CreateCommand("SELECT value FROM runtime_settings WHERE key = $key");
        current.Transaction = transaction;
        current.Parameters.AddWithValue("$key", RulesVersionKey);
        if (current.ExecuteScalar() as string == version)
        {
            transaction.Commit();
            return 0;
        }

        int queued = connection.Run(transaction, """
            INSERT INTO reconsolidation_queue (entity_id, reason, previous_type, enqueued_utc)
            SELECT entity_id, 'rules', NULL, $now FROM knowledge_entities
            WHERE status = 'active' AND type_origin = 'inferred'
            ON CONFLICT(entity_id) DO NOTHING
            """, ("$now", now));
        // Corrections the owner made before reconsolidation existed are replayed once as lessons,
        // taking the type the evidence would have given as what Praxis believed.
        queued += connection.Run(transaction, """
            INSERT INTO reconsolidation_queue (entity_id, reason, previous_type, enqueued_utc)
            SELECT e.entity_id, 'owner_lesson', ranked.entity_type, $now
            FROM knowledge_entities e
            JOIN (SELECT entity_id, entity_type FROM (
                      SELECT m.entity_id, m.entity_type,
                             ROW_NUMBER() OVER (PARTITION BY m.entity_id ORDER BY COUNT(*) DESC, m.entity_type) AS rank
                      FROM visible_entity_mentions m WHERE m.entity_type IS NOT NULL
                      GROUP BY m.entity_id, m.entity_type)
                  WHERE rank = 1) ranked ON ranked.entity_id = e.entity_id
            WHERE e.status = 'active' AND e.type_origin = 'owner' AND ranked.entity_type IS NOT e.entity_type
            ON CONFLICT(entity_id) DO UPDATE SET reason = 'owner_lesson', previous_type = excluded.previous_type
            """, ("$now", now));
        connection.Run(transaction, """
            INSERT INTO runtime_settings (key, value, updated_utc) VALUES ($key, $value, $now)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value, updated_utc = excluded.updated_utc
            """, ("$key", RulesVersionKey), ("$value", version), ("$now", now));
        transaction.Commit();
        return queued;
    });

    public ReconsolidationPass RunPass(int budget)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(budget, 1);
        return database.Execute(connection =>
        {
            string now = CatalogValues.Timestamp(clock.GetUtcNow());
            using SqliteTransaction transaction = connection.BeginTransaction();
            List<(string Entity, string Reason, string? Previous)> batch = [];
            using (SqliteCommand next = connection.CreateCommand("""
                SELECT entity_id, reason, previous_type FROM reconsolidation_queue
                ORDER BY enqueued_utc, entity_id LIMIT $budget
                """))
            {
                next.Transaction = transaction;
                next.Parameters.AddWithValue("$budget", budget);
                using SqliteDataReader reader = next.ExecuteReader();
                while (reader.Read())
                {
                    batch.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
                }
            }

            int examined = 0, applied = 0, suggested = 0;
            foreach ((string entity, string reason, string? previous) in batch)
            {
                connection.Run(transaction, "DELETE FROM reconsolidation_queue WHERE entity_id = $entity", ("$entity", entity));
                if (State(connection, transaction, entity) is not { } state || state.Status != "active")
                {
                    continue;
                }

                examined++;
                suggested += ReconsolidationRules.DuplicateNames(connection, transaction, entity, now);
                suggested += ReconsolidationRules.ImpossibleRelations(connection, transaction, entity, now);
                if (reason == RevisionVocabulary.OwnerLesson && state.Origin == "owner" && previous is not null && state.Type is not null)
                {
                    suggested += ReconsolidationRules.OwnerLessons(connection, transaction, entity, state.Type, previous, now);
                }

                if (state.Origin != "inferred")
                {
                    continue;
                }

                string? type = state.Type;
                string trigger = reason == RevisionVocabulary.OwnerLesson ? RevisionVocabulary.Mentions : reason;
                if (ReconsolidationRules.RefreshInferredType(connection, transaction, entity, type, trigger, now))
                {
                    applied++;
                    type = State(connection, transaction, entity)?.Type;
                }

                if (ReconsolidationRules.RelationSignature(connection, transaction, entity, type, now))
                {
                    suggested++;
                }
            }

            using SqliteCommand remaining = connection.CreateCommand("SELECT COUNT(*) FROM reconsolidation_queue");
            remaining.Transaction = transaction;
            int left = Convert.ToInt32(remaining.ExecuteScalar(), CultureInfo.InvariantCulture);
            transaction.Commit();
            return new ReconsolidationPass(examined, applied, suggested, left);
        });
    }

    public IReadOnlyList<KnowledgeRevision> Revisions(string? state, int limit, long afterRevisionId = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        return database.ExecuteRead(connection =>
        {
            using SqliteCommand query = connection.CreateCommand($"""
                SELECT {KnowledgeRevisionLog.Columns}
                FROM knowledge_revisions r JOIN knowledge_entities e ON e.entity_id = r.entity_id
                WHERE ($state IS NULL OR r.state = $state) AND r.revision_id > $after
                ORDER BY r.revision_id DESC LIMIT $limit
                """);
            query.Parameters.AddWithValue("$state", (object?)state ?? DBNull.Value);
            query.Parameters.AddWithValue("$after", afterRevisionId);
            query.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader reader = query.ExecuteReader();
            List<KnowledgeRevision> revisions = [];
            while (reader.Read())
            {
                revisions.Add(KnowledgeRevisionLog.Read(reader));
            }

            return revisions;
        });
    }

    public KnowledgeRevision? Revision(long revisionId) => database.ExecuteRead(connection =>
    {
        using SqliteCommand query = connection.CreateCommand($"""
            SELECT {KnowledgeRevisionLog.Columns}
            FROM knowledge_revisions r JOIN knowledge_entities e ON e.entity_id = r.entity_id
            WHERE r.revision_id = $id
            """);
        query.Parameters.AddWithValue("$id", revisionId);
        using SqliteDataReader reader = query.ExecuteReader();
        return reader.Read() ? KnowledgeRevisionLog.Read(reader) : null;
    });

    public bool MarkDecided(long revisionId, bool accepted) => database.Execute(connection =>
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        int changed = connection.Run(transaction, """
            UPDATE knowledge_revisions SET state = $state, decided_utc = $now
            WHERE revision_id = $id AND state = 'suggested'
            """, ("$state", accepted ? RevisionVocabulary.Accepted : RevisionVocabulary.Dismissed),
            ("$now", CatalogValues.Timestamp(clock.GetUtcNow())), ("$id", revisionId));
        transaction.Commit();
        return changed == 1;
    });

    public Guid? RelationProposal(Guid relationId) => database.ExecuteRead(connection =>
    {
        using SqliteCommand query = connection.CreateCommand(
            "SELECT proposal_id FROM knowledge_relations WHERE relation_id = $relation AND origin = 'model'");
        query.Parameters.AddWithValue("$relation", relationId.ToString("D"));
        return query.ExecuteScalar() is string proposal ? Guid.Parse(proposal) : (Guid?)null;
    });

    /// <summary>The newest knowledge revision and revision log entry, for clients that refresh on change.</summary>
    public (long GraphRevision, long LatestRevisionId) Watermark() => database.ExecuteSnapshot(connection =>
    {
        using SqliteCommand query = connection.CreateCommand(
            "SELECT (SELECT revision FROM graph_revision WHERE singleton = 1), (SELECT COALESCE(MAX(revision_id), 0) FROM knowledge_revisions)");
        using SqliteDataReader reader = query.ExecuteReader();
        reader.Read();
        return (reader.GetInt64(0), reader.GetInt64(1));
    });

    private static (string Status, string Origin, string? Type)? State(SqliteConnection connection, SqliteTransaction transaction, string entity)
    {
        using SqliteCommand query = connection.CreateCommand(
            "SELECT status, type_origin, entity_type FROM knowledge_entities WHERE entity_id = $entity");
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$entity", entity);
        using SqliteDataReader reader = query.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)) : null;
    }
}
