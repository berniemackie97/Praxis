using Praxis.Inference;

namespace Praxis.Storage;

public sealed record LocalProposal(
    Guid ProposalId, Guid SourceId, Guid ItemId, int Revision, string SourceSha256,
    string Producer, string Kind, string? EntityType, string Value, string Evidence,
    int EvidenceOffset, string Status, string? Correction);

/// <summary>A proposed assertion and the exact span of extracted text that supports it.</summary>
internal sealed record ProposalCandidate(
    string Kind, string? EntityType, string Value, int EvidenceOffset, int EvidenceLength);

/// <summary>Provenance, usage and coverage of a model-produced run.</summary>
internal sealed record ModelRunRecord(
    ModelIdentity Model, int InputTokens, int OutputTokens, TimeSpan Duration,
    int SegmentsExamined, int SegmentsTotal, int Rejected);

/// <summary>
/// Source-grounded proposals (topics and typed entities) with exact evidence spans and
/// append-only owner review. Proposals are visible only for the current, verified revision
/// of an active source; no proposal changes canonical content.
/// </summary>
public sealed class LocalProposals
{
    public const int MaximumPerRun = 64;
    public const int MaximumValueLength = 120;
    public const int MaximumEvidenceLength = 240;

    public static readonly IReadOnlySet<string> EntityTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "person", "organization", "project", "technology", "concept", "place", "document", "event", "other"
    };

    private readonly CatalogDatabase database;

    internal LocalProposals(CatalogDatabase database) => this.database = database;

    /// <summary>
    /// Records the producer run, its proposals and the job's completion in one transaction,
    /// only while the lease is held, the source is active and the revision digest is unchanged.
    /// </summary>
    internal bool Commit(
        ProcessingLease lease, LeasedText input, IReadOnlyList<ProposalCandidate> candidates,
        ModelRunRecord? model = null)
    {
        Validate(input, candidates);
        var now = CatalogValues.Timestamp(DateTimeOffset.UtcNow);
        return database.Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            if (!LocalProcessingQueue.TryCompleteLeased(connection, transaction, lease, input.Digest, now))
            {
                return false;
            }

            using var run = connection.CreateCommand("""
                INSERT INTO enrichment_runs
                    (item_id, revision, processor_key, source_digest, outcome, created_utc,
                     model_provider, model_name, model_digest, input_tokens, output_tokens,
                     duration_ms, segments_examined, segments_total, rejected_count)
                VALUES ($item, $revision, $processor, $digest, $outcome, $now,
                        $provider, $model, $modelDigest, $input, $output,
                        $duration, $examined, $total, $rejected)
                """);
            run.Transaction = transaction;
            ProcessingJobKey.Bind(run, lease.ItemId, lease.Revision, lease.ProcessorKey);
            run.Parameters.AddWithValue("$digest", input.Digest);
            run.Parameters.AddWithValue("$outcome", candidates.Count == 0 ? "abstained" : "generated");
            run.Parameters.AddWithValue("$now", now);
            run.Parameters.AddWithValue("$provider", (object?)model?.Model.Provider ?? DBNull.Value);
            run.Parameters.AddWithValue("$model", (object?)model?.Model.Model ?? DBNull.Value);
            run.Parameters.AddWithValue("$modelDigest", (object?)model?.Model.Digest ?? DBNull.Value);
            run.Parameters.AddWithValue("$input", (object?)model?.InputTokens ?? DBNull.Value);
            run.Parameters.AddWithValue("$output", (object?)model?.OutputTokens ?? DBNull.Value);
            run.Parameters.AddWithValue("$duration",
                model is null ? DBNull.Value : (long)model.Duration.TotalMilliseconds);
            run.Parameters.AddWithValue("$examined", (object?)model?.SegmentsExamined ?? DBNull.Value);
            run.Parameters.AddWithValue("$total", (object?)model?.SegmentsTotal ?? DBNull.Value);
            run.Parameters.AddWithValue("$rejected", (object?)model?.Rejected ?? DBNull.Value);
            run.ExecuteNonQuery();

            foreach (var candidate in candidates)
            {
                using var proposal = connection.CreateCommand("""
                    INSERT INTO enrichment_proposals
                        (proposal_id, item_id, revision, processor_key, source_digest, kind,
                         entity_type, value, evidence, evidence_offset, evidence_length,
                         status, created_utc)
                    VALUES ($id, $item, $revision, $processor, $digest, $kind,
                            $entityType, $value, $evidence, $offset, $length, 'pending', $now)
                    """);
                proposal.Transaction = transaction;
                proposal.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                ProcessingJobKey.Bind(proposal, lease.ItemId, lease.Revision, lease.ProcessorKey);
                proposal.Parameters.AddWithValue("$digest", input.Digest);
                proposal.Parameters.AddWithValue("$kind", candidate.Kind);
                proposal.Parameters.AddWithValue("$entityType", (object?)candidate.EntityType ?? DBNull.Value);
                proposal.Parameters.AddWithValue("$value", candidate.Value);
                proposal.Parameters.AddWithValue("$evidence",
                    input.Content.Substring(candidate.EvidenceOffset, candidate.EvidenceLength));
                proposal.Parameters.AddWithValue("$offset", candidate.EvidenceOffset);
                proposal.Parameters.AddWithValue("$length", candidate.EvidenceLength);
                proposal.Parameters.AddWithValue("$now", now);
                proposal.ExecuteNonQuery();
            }

            transaction.Commit();
            return true;
        });
    }

    private static void Validate(LeasedText input, IReadOnlyList<ProposalCandidate> candidates)
    {
        if (candidates.Count > MaximumPerRun)
        {
            throw new ArgumentException("A run may record at most 64 proposals.", nameof(candidates));
        }

        foreach (var candidate in candidates)
        {
            var kindValid = candidate.Kind switch
            {
                "topic" => candidate.EntityType is null,
                "entity" => candidate.EntityType is not null && EntityTypes.Contains(candidate.EntityType),
                _ => false
            };
            if (!kindValid || candidate.Value.Length is < 1 or > MaximumValueLength ||
                candidate.Value.Any(char.IsControl) ||
                candidate.EvidenceLength is < 1 or > MaximumEvidenceLength ||
                candidate.EvidenceOffset < 0 ||
                candidate.EvidenceOffset > input.Content.Length - candidate.EvidenceLength)
            {
                throw new ArgumentException("Each proposal needs a valid kind, value and evidence span.",
                    nameof(candidates));
            }
        }
    }

    public IReadOnlyList<LocalProposal> List(Guid sourceId, int limit = 40)
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
            using var query = connection.CreateCommand("""
                SELECT proposal_id, source_id, item_id, revision,
                       source_digest, processor_key, kind, entity_type, value, evidence,
                       evidence_offset, status, correction
                FROM eligible_enrichment_proposals
                WHERE source_id = $source
                ORDER BY evidence_offset, proposal_id LIMIT $limit
                """);
            query.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            query.Parameters.AddWithValue("$limit", limit);
            using var reader = query.ExecuteReader();
            var result = new List<LocalProposal>();
            while (reader.Read())
            {
                result.Add(new LocalProposal(
                    Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                    Guid.Parse(reader.GetString(2)), reader.GetInt32(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8),
                    reader.GetString(9), reader.GetInt32(10), reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12)));
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
            if (string.IsNullOrWhiteSpace(correction) || correction.Length > MaximumValueLength ||
                correction.Any(char.IsControl))
            {
                throw new ArgumentException(
                    "Corrections must be printable and at most 120 characters.", nameof(correction));
            }
        }
        else if (correction is not null)
        {
            throw new ArgumentException("Only corrected proposals accept replacement text.", nameof(correction));
        }

        return database.Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var eligible = connection.CreateCommand("""
                SELECT status, correction FROM eligible_enrichment_proposals
                WHERE proposal_id = $id
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

            var now = CatalogValues.Timestamp(DateTimeOffset.UtcNow);
            using var update = connection.CreateCommand("""
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

            using var history = connection.CreateCommand("""
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
    /// A producer's state for the source's current revision: unavailable, pending,
    /// unsupported, failed, or the recorded run outcome (generated / abstained).
    /// </summary>
    public string Outcome(Guid sourceId, string producer = LocalProcessors.MarkdownHeadings)
    {
        ProcessingJobKey.ValidateProcessor(producer);
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("A valid source identifier is required.", nameof(sourceId));
        }

        return database.Execute(connection =>
        {
            using var query = connection.CreateCommand("""
                SELECT s.status, text.state, producer.state, r.outcome
                FROM sources s
                JOIN items i ON i.source_id = s.source_id
                JOIN item_versions v ON v.item_id = i.item_id
                LEFT JOIN processing_jobs text ON text.item_id = v.item_id
                    AND text.revision = v.revision AND text.processor_key = 'source-text-v1'
                LEFT JOIN processing_jobs producer ON producer.item_id = v.item_id
                    AND producer.revision = v.revision AND producer.processor_key = $producer
                LEFT JOIN enrichment_runs r ON r.item_id = v.item_id
                    AND r.revision = v.revision AND r.source_digest = v.digest
                    AND r.processor_key = $producer
                WHERE s.source_id = $source
                  AND v.revision = (SELECT MAX(latest.revision)
                                    FROM item_versions latest WHERE latest.item_id = v.item_id)
                LIMIT 1
                """);
            query.Parameters.AddWithValue("$producer", producer);
            query.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            using var reader = query.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != "active")
            {
                return "unavailable";
            }

            if (!reader.IsDBNull(3))
            {
                return reader.GetString(3);
            }

            var textState = reader.IsDBNull(1) ? null : reader.GetString(1);
            var producerState = reader.IsDBNull(2) ? null : reader.GetString(2);
            return (producerState ?? textState) switch
            {
                "unsupported" => "unsupported",
                "failed" or "canceled" => "failed",
                _ => "pending"
            };
        });
    }
}
