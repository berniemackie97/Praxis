namespace Praxis.Storage;

public sealed partial class LocalCatalog
{
    public bool ReviewTopicProposal(Guid proposalId, string decision, string? correction = null)
    {
        if (proposalId == Guid.Empty)
        {
            throw new ArgumentException("A proposal identifier is required.", nameof(proposalId));
        }

        if (decision is not ("accepted" or "rejected" or "corrected"))
        {
            throw new ArgumentException("The review decision is invalid.", nameof(decision));
        }

        if (decision == "corrected")
        {
            correction = correction?.Trim();
            if (string.IsNullOrWhiteSpace(correction) || correction.Length > 80 ||
                correction.Any(char.IsControl))
            {
                throw new ArgumentException(
                    "Corrections must be printable and at most 80 characters.", nameof(correction));
            }
        }
        else if (correction is not null)
        {
            throw new ArgumentException("Only corrected proposals accept replacement text.", nameof(correction));
        }

        lock (connection)
        {
            using var transaction = connection.BeginTransaction();
            using var eligible = Command(connection, """
                SELECT p.status, p.correction
                FROM enrichment_proposals p
                JOIN item_versions v ON v.item_id = p.item_id AND v.revision = p.revision
                    AND v.digest = p.source_digest
                JOIN items i ON i.item_id = p.item_id
                JOIN sources s ON s.source_id = i.source_id
                JOIN text_derivatives d ON d.item_id = p.item_id
                    AND d.revision = p.revision AND d.source_digest = p.source_digest
                    AND d.processor_key = 'source-text-v1'
                JOIN processing_jobs j ON j.item_id = d.item_id
                    AND j.revision = d.revision AND j.processor_key = d.processor_key
                WHERE p.proposal_id = $id AND s.status = 'active'
                  AND j.state = 'completed' AND d.outcome = 'completed'
                  AND p.revision = (SELECT MAX(v2.revision) FROM item_versions v2
                                    WHERE v2.item_id = p.item_id)
                """);
            eligible.Transaction = transaction;
            eligible.Parameters.AddWithValue("$id", proposalId.ToString("D"));
            string status;
            string? previousCorrection;
            using (var reader = eligible.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return false;
                }

                status = reader.GetString(0);
                previousCorrection = reader.IsDBNull(1) ? null : reader.GetString(1);
            }

            if (status == decision && previousCorrection == correction)
            {
                return false;
            }

            var now = Timestamp(DateTimeOffset.UtcNow);
            using var update = Command(connection, """
                UPDATE enrichment_proposals SET status = $decision,
                    correction = $correction, reviewed_utc = $now WHERE proposal_id = $id
                """);
            update.Transaction = transaction;
            update.Parameters.AddWithValue("$id", proposalId.ToString("D"));
            update.Parameters.AddWithValue("$decision", decision);
            update.Parameters.AddWithValue("$correction", (object?)correction ?? DBNull.Value);
            update.Parameters.AddWithValue("$now", now);
            if (update.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("The proposal review was not saved.");
            }

            using var history = Command(connection, """
                INSERT INTO enrichment_reviews
                    (review_id, proposal_id, decision, correction, reviewed_utc)
                VALUES ($review, $id, $decision, $correction, $now)
                """);
            history.Transaction = transaction;
            history.Parameters.AddWithValue("$review", Guid.NewGuid().ToString("D"));
            history.Parameters.AddWithValue("$id", proposalId.ToString("D"));
            history.Parameters.AddWithValue("$decision", decision);
            history.Parameters.AddWithValue("$correction", (object?)correction ?? DBNull.Value);
            history.Parameters.AddWithValue("$now", now);
            history.ExecuteNonQuery();
            transaction.Commit();
            return true;
        }
    }
}
