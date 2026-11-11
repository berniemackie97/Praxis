using Microsoft.Data.Sqlite;
using Praxis.Infrastructure.Knowledge;
using Praxis.Infrastructure.Persistence;

namespace Praxis.Infrastructure.Sources;

/// <summary>
/// Copies owner review decisions from one source's current proposals to the matching pending
/// proposals of another source (same producer, kind, value, relation ends and evidence text),
/// recording each copy in the review history. Used when two identities turn out to be the
/// same file, so decisions made on either survive.
/// </summary>
internal static class ReviewCarryOver
{
    internal static void Apply(SqliteConnection connection, SqliteTransaction transaction, string fromSourceId, string toSourceId, string now)
    {
        using SqliteCommand query = connection.CreateCommand("""
                                                             SELECT target.proposal_id, target.kind, reviewed.status, reviewed.correction
                                                             FROM eligible_enrichment_proposals reviewed
                                                             JOIN eligible_enrichment_proposals target
                                                               ON target.source_id = $to AND target.status = 'pending'
                                                              AND target.processor_key = reviewed.processor_key AND target.kind = reviewed.kind
                                                              AND target.value = reviewed.value AND target.evidence = reviewed.evidence
                                                              AND COALESCE(target.relation_subject, '') = COALESCE(reviewed.relation_subject, '')
                                                              AND COALESCE(target.relation_object, '') = COALESCE(reviewed.relation_object, '')
                                                             WHERE reviewed.source_id = $from AND reviewed.status <> 'pending'
                                                             """);
        query.Transaction = transaction;
        query.Parameters.AddWithValue("$from", fromSourceId);
        query.Parameters.AddWithValue("$to", toSourceId);
        List<(string Proposal, string Kind, string Status, string? Correction)> decisions = new();
        using (SqliteDataReader reader = query.ExecuteReader())
        {
            while (reader.Read())
            {
                decisions.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        foreach ((string proposal, string kind, string status, string? correction) in decisions)
        {
            connection.Run(transaction, """
                                        UPDATE enrichment_proposals SET status = $status, correction = $correction, reviewed_utc = $now
                                        WHERE proposal_id = $proposal AND status = 'pending';
                                        INSERT INTO enrichment_reviews (review_id, proposal_id, decision, correction, reviewed_utc)
                                        VALUES ($review, $proposal, $status, $correction, $now);
                                        """, ("$status", status), ("$correction", correction), ("$now", now), ("$proposal", proposal),
                ("$review", Guid.NewGuid().ToString("D")));
            if (kind != "entity")
            {
                continue;
            }

            if (correction is not null)
            {
                KnowledgeLinker.Relink(connection, transaction, proposal, correction, now);
            }

            using SqliteCommand linked = connection.CreateCommand("SELECT entity_id FROM entity_mentions WHERE proposal_id = $proposal");
            linked.Transaction = transaction;
            linked.Parameters.AddWithValue("$proposal", proposal);
            if (linked.ExecuteScalar() is string entity)
            {
                KnowledgeLinker.RecomputeInferredTypes(connection, transaction, [entity], now);
            }
        }
    }
}
