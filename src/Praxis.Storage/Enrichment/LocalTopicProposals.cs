using Microsoft.Data.Sqlite;
using Praxis.Storage.Catalog;
using Praxis.Storage.Processing;

namespace Praxis.Storage.Enrichment;

public sealed record LocalTopicProposal(Guid ProposalId, Guid SourceId, Guid ItemId, int Revision, string SourceSha256, string Producer, string Value, string Evidence, int EvidenceOffset, string Status, string? Correction);

internal sealed record TopicCandidate(Guid ItemId, int Revision, string Digest, string Content, string SourceKind, string Locator);
internal sealed record TopicEvidence(string Value, int Offset);

/// <summary>
/// Source-grounded topic proposals with exact evidence spans and append-only owner review.
/// Proposals are visible only for the current, verified revision of an active source.
/// </summary>
public sealed class LocalTopicProposals
{
    private readonly CatalogDatabase database;

    internal LocalTopicProposals(CatalogDatabase database) => this.database = database;

    /// <summary>
    /// Verified extracted text for a leased heading job, or null when the lease is no longer
    /// held, the source was revoked or the text derivative does not match the revision.
    /// </summary>
    internal TopicCandidate? ReadLeasedCandidate(ProcessingLease lease)
    {
        ProcessingJobKey.Validate(lease.ItemId, lease.Revision, lease.ProcessorKey);
        return database.Execute(connection =>
        {
            using SqliteCommand query = connection.CreateCommand("""
                                                                 SELECT d.source_digest, d.content, s.source_kind, s.locator
                                                                 FROM processing_jobs j
                                                                 JOIN text_derivatives d ON d.item_id = j.item_id AND d.revision = j.revision
                                                                     AND d.processor_key = 'source-text-v1' AND d.outcome = 'completed'
                                                                 JOIN processing_jobs t ON t.item_id = d.item_id AND t.revision = d.revision
                                                                     AND t.processor_key = d.processor_key AND t.state = 'completed'
                                                                 JOIN item_versions v ON v.item_id = d.item_id AND v.revision = d.revision
                                                                     AND v.digest = d.source_digest
                                                                 JOIN items i ON i.item_id = j.item_id
                                                                 JOIN sources s ON s.source_id = i.source_id
                                                                 WHERE j.item_id = $item AND j.revision = $revision
                                                                   AND j.processor_key = $processor AND j.state = 'running'
                                                                   AND j.lease_key = $lease AND j.lease_until_utc > $now
                                                                   AND s.status = 'active'
                                                                 """);
            ProcessingJobKey.BindLease(query, lease);
            query.Parameters.AddWithValue("$now", CatalogValues.Timestamp(DateTimeOffset.UtcNow));
            using SqliteDataReader reader = query.ExecuteReader();
            return reader.Read()
                ? new TopicCandidate(lease.ItemId, lease.Revision, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))
                : null;
        });
    }

    /// <summary>
    /// Records the producer run, its proposals and the job's completion in one transaction,
    /// only while the lease is still held and the source is active.
    /// </summary>
    internal bool CommitCandidates(ProcessingLease lease, TopicCandidate candidate, IReadOnlyList<TopicEvidence> evidence)
    {
        if (candidate.ItemId != lease.ItemId || candidate.Revision != lease.Revision || evidence.Count > 8 || evidence.Any(e => e.Offset < 0
                || e.Value.Length is < 3 or > 80 || e.Offset > candidate.Content.Length - e.Value.Length || !candidate.Content.AsSpan(e.Offset, e.Value.Length).SequenceEqual(e.Value.AsSpan())))
        {
            throw new ArgumentException("Topic evidence must match its extracted source span.");
        }

        string now = CatalogValues.Timestamp(DateTimeOffset.UtcNow);
        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand finish = connection.CreateCommand("""
                                                                  UPDATE processing_jobs
                                                                  SET state = 'completed', lease_key = NULL, lease_until_utc = NULL,
                                                                      failure_code = NULL, updated_utc = $now
                                                                  WHERE item_id = $item AND revision = $revision
                                                                    AND processor_key = $processor AND state = 'running'
                                                                    AND lease_key = $lease AND lease_until_utc > $now
                                                                    AND EXISTS (
                                                                        SELECT 1 FROM items i JOIN sources s ON s.source_id = i.source_id
                                                                        JOIN item_versions v ON v.item_id = i.item_id
                                                                        WHERE i.item_id = processing_jobs.item_id AND s.status = 'active'
                                                                          AND v.revision = processing_jobs.revision AND v.digest = $digest)
                                                                  """);
            finish.Transaction = transaction;
            ProcessingJobKey.BindLease(finish, lease);
            finish.Parameters.AddWithValue("$digest", candidate.Digest);
            finish.Parameters.AddWithValue("$now", now);
            if (finish.ExecuteNonQuery() != 1)
            {
                return false;
            }

            using SqliteCommand run = connection.CreateCommand("""
                                                               INSERT INTO enrichment_runs
                                                                   (item_id, revision, processor_key, source_digest, outcome, created_utc)
                                                               VALUES ($item, $revision, $processor, $digest, $outcome, $now)
                                                               """);
            run.Transaction = transaction;
            ProcessingJobKey.Bind(run, lease.ItemId, lease.Revision, lease.ProcessorKey);
            run.Parameters.AddWithValue("$digest", candidate.Digest);
            run.Parameters.AddWithValue("$outcome", evidence.Count == 0 ? "abstained" : "generated");
            run.Parameters.AddWithValue("$now", now);
            run.ExecuteNonQuery();

            foreach (TopicEvidence item in evidence)
            {
                using SqliteCommand proposal = connection.CreateCommand("""
                                                                        INSERT INTO enrichment_proposals
                                                                            (proposal_id, item_id, revision, processor_key, source_digest,
                                                                             kind, value, evidence, evidence_offset, status, created_utc)
                                                                        VALUES ($id, $item, $revision, $processor, $digest,
                                                                                'topic', $value, $evidence, $offset, 'pending', $now)
                                                                        """);
                proposal.Transaction = transaction;
                proposal.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                ProcessingJobKey.Bind(proposal, lease.ItemId, lease.Revision, lease.ProcessorKey);
                proposal.Parameters.AddWithValue("$digest", candidate.Digest);
                proposal.Parameters.AddWithValue("$value", item.Value);
                proposal.Parameters.AddWithValue("$evidence", item.Value);
                proposal.Parameters.AddWithValue("$offset", item.Offset);
                proposal.Parameters.AddWithValue("$now", now);
                proposal.ExecuteNonQuery();
            }

            LocalProcessingQueue.QueueDependents(connection, transaction, lease.ItemId, lease.Revision, lease.ProcessorKey, now);
            transaction.Commit();
            return true;
        });
    }

    public IReadOnlyList<LocalTopicProposal> List(Guid sourceId, int limit = 40)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("A valid source identifier is required.", nameof(sourceId));
        }

        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return database.Execute(connection =>
        {
            using SqliteCommand query = connection.CreateCommand("""
                                                                 SELECT proposal_id, source_id, item_id, revision,
                                                                        source_digest, processor_key, value, evidence,
                                                                        evidence_offset, status, correction
                                                                 FROM eligible_enrichment_proposals
                                                                 WHERE source_id = $source
                                                                 ORDER BY evidence_offset, proposal_id LIMIT $limit
                                                                 """);
            query.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            query.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader reader = query.ExecuteReader();
            List<LocalTopicProposal> result = new();
            while (reader.Read())
            {
                result.Add(new LocalTopicProposal(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                    Guid.Parse(reader.GetString(2)), reader.GetInt32(3), reader.GetString(4),
                    reader.GetString(5), reader.GetString(6), reader.GetString(7),
                    reader.GetInt32(8), reader.GetString(9), reader.IsDBNull(10)
                        ? null
                        : reader.GetString(10)));
            }

            return result;
        });
    }

    public bool Review(Guid proposalId, string decision, string? correction = null)
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
            if (string.IsNullOrWhiteSpace(correction) || correction.Length > 80 || correction.Any(char.IsControl))
            {
                throw new ArgumentException("Corrections must be printable and at most 80 characters.", nameof(correction));
            }
        }
        else if (correction is not null)
        {
            throw new ArgumentException("Only corrected proposals accept replacement text.", nameof(correction));
        }

        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand eligible = connection.CreateCommand("""
                                                                    SELECT status, correction FROM eligible_enrichment_proposals
                                                                    WHERE proposal_id = $id
                                                                    """);
            eligible.Transaction = transaction;
            eligible.Parameters.AddWithValue("$id", proposalId.ToString("D"));
            string status;
            string? previousCorrection;
            using (SqliteDataReader reader = eligible.ExecuteReader())
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

            string now = CatalogValues.Timestamp(DateTimeOffset.UtcNow);
            using SqliteCommand update = connection.CreateCommand("""
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

            using SqliteCommand history = connection.CreateCommand("""
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
        });
    }

    /// <summary>
    /// Heading-producer state of the source's current revision: unavailable, pending,
    /// unsupported, failed, or the recorded run outcome (generated / abstained).
    /// </summary>
    public string Outcome(Guid sourceId)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("A valid source identifier is required.", nameof(sourceId));
        }

        return database.Execute(connection =>
        {
            using SqliteCommand query = connection.CreateCommand("""
                                                                 SELECT s.status, text.state, heading.state, r.outcome
                                                                 FROM sources s
                                                                 JOIN items i ON i.source_id = s.source_id
                                                                 JOIN item_versions v ON v.item_id = i.item_id
                                                                 LEFT JOIN processing_jobs text ON text.item_id = v.item_id
                                                                     AND text.revision = v.revision AND text.processor_key = 'source-text-v1'
                                                                 LEFT JOIN processing_jobs heading ON heading.item_id = v.item_id
                                                                     AND heading.revision = v.revision AND heading.processor_key = $producer
                                                                 LEFT JOIN enrichment_runs r ON r.item_id = v.item_id
                                                                     AND r.revision = v.revision AND r.source_digest = v.digest
                                                                     AND r.processor_key = $producer
                                                                 WHERE s.source_id = $source
                                                                   AND v.revision = (SELECT MAX(latest.revision)
                                                                                     FROM item_versions latest WHERE latest.item_id = v.item_id)
                                                                 LIMIT 1
                                                                 """);
            query.Parameters.AddWithValue("$producer", LocalProcessors.MarkdownHeadings);
            query.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            using SqliteDataReader reader = query.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != "active")
            {
                return "unavailable";
            }

            if (!reader.IsDBNull(3))
            {
                return reader.GetString(3);
            }

            string? textState = reader.IsDBNull(1) ? null : reader.GetString(1);
            string? headingState = reader.IsDBNull(2) ? null : reader.GetString(2);
            return (headingState ?? textState) switch
            {
                "unsupported" => "unsupported",
                "failed" or "canceled" => "failed",
                _ => "pending"
            };
        });
    }
}
