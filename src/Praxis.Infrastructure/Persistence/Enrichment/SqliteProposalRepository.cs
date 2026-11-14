using Microsoft.Data.Sqlite;
using Praxis.Application.Enrichment;
using Praxis.Application.Text;
using Praxis.Domain.Enrichment;
using Praxis.Domain.Processing;
using Praxis.Domain.Text;
using Praxis.Infrastructure.Persistence.Knowledge;
using Praxis.Infrastructure.Persistence.Processing;

namespace Praxis.Infrastructure.Persistence.Enrichment;

/// <summary>
/// Source-grounded proposals (topics and typed entities) with exact evidence spans and
/// append-only owner review. Proposals are visible only for the current, verified revision
/// of an active source; no proposal changes canonical content.
/// </summary>
public sealed class SqliteProposalRepository : IProposalRepository
{
    private readonly CatalogDatabase database;
    private readonly TimeProvider clock;

    internal SqliteProposalRepository(CatalogDatabase database, TimeProvider clock)
    {
        this.database = database;
        this.clock = clock;
    }

    /// <summary>
    /// Records the producer run, its proposals and the job's completion in one transaction,
    /// only while the lease is held, the source is active and the revision digest is unchanged.
    /// </summary>
    public bool Commit(
        ProcessingLease lease, LeasedText input, IReadOnlyList<ProposalCandidate> candidates,
        ModelRunRecord? model = null)
    {
        ProposalRules.ValidateCandidates(input.Content, candidates);
        string now = CatalogValues.Timestamp(clock.GetUtcNow());
        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            if (!SqliteProcessingQueue.TryCompleteLeased(connection, transaction, lease, input.Digest, now))
            {
                return false;
            }

            Write(connection, transaction, lease, input, candidates, model, now);
            transaction.Commit();
            return true;
        });
    }

    /// <summary>
    /// Writes a producer run and its proposals inside the caller's transaction (after the caller
    /// has completed the leased job) and links entity mentions into the knowledge graph.
    /// </summary>
    internal static void Write(SqliteConnection connection, SqliteTransaction transaction, ProcessingLease lease, LeasedText input,
        IReadOnlyList<ProposalCandidate> candidates, ModelRunRecord? model, string now)
    {
        ProposalRules.ValidateCandidates(input.Content, candidates);
        using SqliteCommand run = connection.CreateCommand("""
                                                           INSERT INTO enrichment_runs
                                                               (item_id, revision, processor_key, source_digest, outcome, created_utc,
                                                                model_provider, model_name, model_digest, input_tokens, output_tokens,
                                                                duration_ms, segments_examined, segments_total, rejected_count, segments_reused)
                                                           VALUES ($item, $revision, $processor, $digest, $outcome, $now,
                                                                   $provider, $model, $modelDigest, $input, $output,
                                                                   $duration, $examined, $total, $rejected, $reused)
                                                           """);
        run.Transaction = transaction;
        ProcessingJobParameters.Bind(run, lease.ItemId, lease.Revision, lease.ProcessorKey);
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
        run.Parameters.AddWithValue("$reused", (object?)model?.SegmentsReused ?? DBNull.Value);
        run.ExecuteNonQuery();

        List<(string ProposalId, ProposalCandidate Candidate)> recorded = new List<(string ProposalId, ProposalCandidate Candidate)>(candidates.Count);
        foreach (ProposalCandidate candidate in candidates)
        {
            string proposalId = Guid.NewGuid().ToString("D");
            using SqliteCommand proposal = connection.CreateCommand("""
                                                                    INSERT INTO enrichment_proposals
                                                                        (proposal_id, item_id, revision, processor_key, source_digest, kind,
                                                                         entity_type, relation_subject, relation_object, value, evidence,
                                                                         evidence_offset, evidence_length, flags, status, created_utc)
                                                                    VALUES ($id, $item, $revision, $processor, $digest, $kind,
                                                                            $entityType, $subject, $object, $value, $evidence,
                                                                            $offset, $length, $flags, 'pending', $now)
                                                                    """);
            proposal.Transaction = transaction;
            proposal.Parameters.AddWithValue("$id", proposalId);
            ProcessingJobParameters.Bind(proposal, lease.ItemId, lease.Revision, lease.ProcessorKey);
            proposal.Parameters.AddWithValue("$digest", input.Digest);
            proposal.Parameters.AddWithValue("$kind", candidate.Kind);
            proposal.Parameters.AddWithValue("$entityType", (object?)candidate.EntityType ?? DBNull.Value);
            proposal.Parameters.AddWithValue("$subject", (object?)candidate.RelationSubject ?? DBNull.Value);
            proposal.Parameters.AddWithValue("$object", (object?)candidate.RelationObject ?? DBNull.Value);
            proposal.Parameters.AddWithValue("$value", candidate.Value);
            proposal.Parameters.AddWithValue("$evidence",
                input.Content.Substring(candidate.EvidenceOffset, candidate.EvidenceLength));
            proposal.Parameters.AddWithValue("$offset", candidate.EvidenceOffset);
            proposal.Parameters.AddWithValue("$length", candidate.EvidenceLength);
            proposal.Parameters.AddWithValue("$flags", (object?)candidate.Flags ?? DBNull.Value);
            proposal.Parameters.AddWithValue("$now", now);
            proposal.ExecuteNonQuery();
            recorded.Add((proposalId, candidate));
        }

        KnowledgeLinker.Link(connection, transaction, recorded, now);
    }

    /// <summary>
    /// The latest earlier revision's result from <paramref name="producer"/> for this item, with
    /// that revision's text and segments, or null when there is none.
    /// </summary>
    public PriorExtraction? ReadPrior(Guid itemId, int revision, string producer) => database.Execute(connection =>
    {
        using SqliteCommand run = connection.CreateCommand("""
                                                           SELECT r.revision, d.content FROM enrichment_runs r
                                                           JOIN text_derivatives d ON d.item_id = r.item_id AND d.revision = r.revision
                                                               AND d.processor_key = 'source-text-v1' AND d.outcome = 'completed'
                                                               AND d.source_digest = r.source_digest
                                                           WHERE r.item_id = $item AND r.revision < $revision AND r.processor_key = $producer
                                                           ORDER BY r.revision DESC LIMIT 1
                                                           """);
        run.Parameters.AddWithValue("$item", itemId.ToString("D"));
        run.Parameters.AddWithValue("$revision", revision);
        run.Parameters.AddWithValue("$producer", producer);
        int priorRevision;
        string content;
        using (SqliteDataReader reader = run.ExecuteReader())
        {
            if (!reader.Read())
            {
                return null;
            }

            priorRevision = reader.GetInt32(0);
            content = reader.GetString(1);
        }

        using SqliteCommand segments = connection.CreateCommand("""
                                                                SELECT start_offset, length, heading_path FROM text_segments
                                                                WHERE item_id = $item AND revision = $revision ORDER BY ordinal
                                                                """);
        segments.Parameters.AddWithValue("$item", itemId.ToString("D"));
        segments.Parameters.AddWithValue("$revision", priorRevision);
        List<TextSegmentSpan> spans = new List<TextSegmentSpan>();
        using (SqliteDataReader reader = segments.ExecuteReader())
        {
            while (reader.Read())
            {
                spans.Add(new TextSegmentSpan(reader.GetInt32(0), reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        using SqliteCommand proposals = connection.CreateCommand("""
                                                                 SELECT kind, entity_type, value, evidence_offset, evidence_length,
                                                                        relation_subject, relation_object, flags
                                                                 FROM enrichment_proposals
                                                                 WHERE item_id = $item AND revision = $revision AND processor_key = $producer
                                                                 ORDER BY evidence_offset, kind, value
                                                                 """);
        proposals.Parameters.AddWithValue("$item", itemId.ToString("D"));
        proposals.Parameters.AddWithValue("$revision", priorRevision);
        proposals.Parameters.AddWithValue("$producer", producer);
        List<ProposalCandidate> candidates = new List<ProposalCandidate>();
        using (SqliteDataReader reader = proposals.ExecuteReader())
        {
            while (reader.Read())
            {
                candidates.Add(new ProposalCandidate(reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2),
                    reader.GetInt32(3), reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }
        }

        return new PriorExtraction(content, spans, candidates);
    });

    public IReadOnlyList<EnrichmentProposal> List(Guid sourceId, int limit = 40)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("A valid source identifier is required.", nameof(sourceId));
        }

        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return database.ExecuteRead(connection =>
        {
            using SqliteCommand query = connection.CreateCommand("""
                                                                 SELECT p.proposal_id, p.source_id, p.item_id, p.revision,
                                                                        p.source_digest, p.processor_key, p.kind, p.entity_type, p.value, p.evidence,
                                                                        p.evidence_offset, p.status, p.correction, p.relation_subject, p.relation_object, p.flags,
                                                                        CASE WHEN p.status IN ('accepted', 'corrected') OR COALESCE(v.verdict, '') = 'supported'
                                                                            OR (p.processor_key = 'intake-understand-v1' AND EXISTS (
                                                                                SELECT 1 FROM intake_documents d WHERE d.item_id = p.item_id AND d.revision = p.revision
                                                                                  AND d.tier IN ('strong', 'strongest', 'reused'))) THEN 1 ELSE 0 END
                                                                 FROM eligible_enrichment_proposals p
                                                                 LEFT JOIN proposal_verdicts v ON v.proposal_id = p.proposal_id
                                                                 WHERE p.source_id = $source AND p.status <> 'rejected'
                                                                   AND p.processor_key <> 'markdown-heading-v1'
                                                                 ORDER BY 17 DESC, p.evidence_offset, p.proposal_id LIMIT $limit
                                                                 """);
            query.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            query.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader reader = query.ExecuteReader();
            List<EnrichmentProposal> result = new List<EnrichmentProposal>();
            while (reader.Read())
            {
                result.Add(new EnrichmentProposal(
                    Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                    Guid.Parse(reader.GetString(2)), reader.GetInt32(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8),
                    reader.GetString(9), reader.GetInt32(10), reader.GetString(11),
                    reader.IsDBNull(12) ? null : reader.GetString(12),
                    reader.IsDBNull(13) ? null : reader.GetString(13),
                    reader.IsDBNull(14) ? null : reader.GetString(14),
                    reader.IsDBNull(15) ? null : reader.GetString(15), reader.GetInt64(16) == 1));
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

        correction = ProposalRules.NormalizeReview(decision, correction);

        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand eligible = connection.CreateCommand("""
                                                                    SELECT status, correction, kind FROM eligible_enrichment_proposals
                                                                    WHERE proposal_id = $id
                                                                    """);
            eligible.Transaction = transaction;
            eligible.Parameters.AddWithValue("$id", proposalId.ToString("D"));
            string status;
            string? previousCorrection;
            string kind;
            using (SqliteDataReader reader = eligible.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return false;
                }

                status = reader.GetString(0);
                previousCorrection = reader.IsDBNull(1) ? null : reader.GetString(1);
                kind = reader.GetString(2);
            }

            if (kind == "relation" && decision == "corrected")
            {
                throw new ArgumentException("Relations can be accepted or rejected, not renamed.", nameof(decision));
            }

            if (status == decision && previousCorrection == correction)
            {
                return false;
            }

            string now = CatalogValues.Timestamp(clock.GetUtcNow());
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
            if (kind == "entity")
            {
                // A correction renames the mention's entity; any decision can change visibility
                // and therefore the inferred type of the entity it is linked to.
                if (correction is not null)
                {
                    KnowledgeLinker.Relink(connection, transaction, proposalId.ToString("D"), correction, now);
                }

                using SqliteCommand linked = connection.CreateCommand(
                    "SELECT entity_id FROM entity_mentions WHERE proposal_id = $id");
                linked.Transaction = transaction;
                linked.Parameters.AddWithValue("$id", proposalId.ToString("D"));
                if (linked.ExecuteScalar() is string entity)
                {
                    KnowledgeLinker.RecomputeInferredTypes(connection, transaction, [entity], now);
                }
            }

            transaction.Commit();
            return true;
        });
    }

    /// <summary>
    /// A producer's state for the source's current revision: unavailable, pending,
    /// unsupported, failed, or the recorded run outcome (generated / abstained).
    /// </summary>
    public string Outcome(Guid sourceId, string producer = ProcessorKeys.SemanticExtraction)
    {
        ProcessingJobKey.ValidateProcessor(producer);
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("A valid source identifier is required.", nameof(sourceId));
        }

        return database.ExecuteRead(connection =>
        {
            using SqliteCommand query = connection.CreateCommand("""
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
            string? producerState = reader.IsDBNull(2) ? null : reader.GetString(2);
            return (producerState ?? textState) switch
            {
                "unsupported" => "unsupported",
                "failed" or "canceled" => "failed",
                _ => "pending"
            };
        });
    }
}
