namespace Praxis.Storage;

public sealed record LocalTopicProposal(
    Guid ProposalId, Guid SourceId, Guid ItemId, int Revision, string SourceSha256,
    string Producer, string Value, string Evidence, int EvidenceOffset,
    string Status, string? Correction);

internal sealed record TopicCandidate(Guid ItemId, int Revision, string Digest, string Content);
internal sealed record TopicEvidence(string Value, int Offset);

/// <summary>
/// Source-grounded topic proposals with exact evidence spans and append-only owner review.
/// Proposals are visible only for the current, verified revision of an active source.
/// </summary>
public sealed class LocalTopicProposals
{
    private readonly CatalogDatabase database;

    internal LocalTopicProposals(CatalogDatabase database) => this.database = database;

    internal TopicCandidate? NextCandidate()
    {
        return database.Execute(connection =>
        {
            using var query = connection.CreateCommand("""
                SELECT d.item_id, d.revision, d.source_digest, d.content
                FROM text_derivatives d
                JOIN processing_jobs j ON j.item_id = d.item_id AND j.revision = d.revision
                    AND j.processor_key = d.processor_key
                JOIN item_versions v ON v.item_id = d.item_id AND v.revision = d.revision
                    AND v.digest = d.source_digest
                JOIN items i ON i.item_id = d.item_id
                JOIN sources s ON s.source_id = i.source_id
                WHERE d.processor_key = 'source-text-v1' AND d.outcome = 'completed'
                  AND j.state = 'completed' AND s.status = 'active' AND s.source_kind = 'file'
                  AND (lower(s.locator) LIKE '%.md' OR lower(s.locator) LIKE '%.markdown')
                  AND d.revision = (SELECT MAX(v2.revision) FROM item_versions v2
                                    WHERE v2.item_id = d.item_id)
                  AND NOT EXISTS (
                      SELECT 1 FROM enrichment_runs r WHERE r.item_id = d.item_id
                      AND r.revision = d.revision AND r.processor_key = $producer)
                ORDER BY d.created_utc, d.item_id LIMIT 1
                """);
            query.Parameters.AddWithValue("$producer", LocalProcessors.MarkdownHeadings);
            using var reader = query.ExecuteReader();
            return reader.Read() ? new TopicCandidate(Guid.Parse(reader.GetString(0)),
                reader.GetInt32(1), reader.GetString(2), reader.GetString(3)) : null;
        });
    }

    internal bool CommitCandidates(TopicCandidate candidate, IReadOnlyList<TopicEvidence> evidence)
    {
        if (evidence.Count > 8 || evidence.Any(e => e.Offset < 0 ||
            e.Value.Length is < 3 or > 80 ||
            e.Offset > candidate.Content.Length - e.Value.Length ||
            !candidate.Content.AsSpan(e.Offset, e.Value.Length).SequenceEqual(e.Value.AsSpan())))
        {
            throw new ArgumentException("Topic evidence must match its extracted source span.");
        }

        return database.Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var eligible = connection.CreateCommand("""
                SELECT 1 FROM text_derivatives d
                JOIN processing_jobs j ON j.item_id = d.item_id AND j.revision = d.revision
                    AND j.processor_key = d.processor_key
                JOIN item_versions v ON v.item_id = d.item_id AND v.revision = d.revision
                    AND v.digest = d.source_digest
                JOIN items i ON i.item_id = d.item_id
                JOIN sources s ON s.source_id = i.source_id
                WHERE d.item_id = $item AND d.revision = $revision
                  AND d.source_digest = $digest AND d.processor_key = 'source-text-v1'
                  AND d.outcome = 'completed' AND j.state = 'completed'
                  AND s.status = 'active'
                  AND d.revision = (SELECT MAX(v2.revision) FROM item_versions v2
                                    WHERE v2.item_id = d.item_id)
                  AND NOT EXISTS (SELECT 1 FROM enrichment_runs r
                      WHERE r.item_id = d.item_id AND r.revision = d.revision
                      AND r.processor_key = $producer)
                LIMIT 1
                """);
            eligible.Transaction = transaction;
            eligible.Parameters.AddWithValue("$item", candidate.ItemId.ToString("D"));
            eligible.Parameters.AddWithValue("$revision", candidate.Revision);
            eligible.Parameters.AddWithValue("$digest", candidate.Digest);
            eligible.Parameters.AddWithValue("$producer", LocalProcessors.MarkdownHeadings);
            if (eligible.ExecuteScalar() is null)
            {
                return false;
            }

            var now = CatalogValues.Timestamp(DateTimeOffset.UtcNow);
            using var run = connection.CreateCommand("""
                INSERT INTO enrichment_runs
                    (item_id, revision, processor_key, source_digest, outcome, created_utc)
                VALUES ($item, $revision, $producer, $digest, $outcome, $now)
                """);
            run.Transaction = transaction;
            run.Parameters.AddWithValue("$item", candidate.ItemId.ToString("D"));
            run.Parameters.AddWithValue("$revision", candidate.Revision);
            run.Parameters.AddWithValue("$producer", LocalProcessors.MarkdownHeadings);
            run.Parameters.AddWithValue("$digest", candidate.Digest);
            run.Parameters.AddWithValue("$outcome", evidence.Count == 0 ? "abstained" : "generated");
            run.Parameters.AddWithValue("$now", now);
            run.ExecuteNonQuery();

            foreach (var item in evidence)
            {
                using var proposal = connection.CreateCommand("""
                    INSERT INTO enrichment_proposals
                        (proposal_id, item_id, revision, processor_key, source_digest,
                         kind, value, evidence, evidence_offset, status, created_utc)
                    VALUES ($id, $item, $revision, $producer, $digest,
                            'topic', $value, $evidence, $offset, 'pending', $now)
                    """);
                proposal.Transaction = transaction;
                proposal.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                proposal.Parameters.AddWithValue("$item", candidate.ItemId.ToString("D"));
                proposal.Parameters.AddWithValue("$revision", candidate.Revision);
                proposal.Parameters.AddWithValue("$producer", LocalProcessors.MarkdownHeadings);
                proposal.Parameters.AddWithValue("$digest", candidate.Digest);
                proposal.Parameters.AddWithValue("$value", item.Value);
                proposal.Parameters.AddWithValue("$evidence", item.Value);
                proposal.Parameters.AddWithValue("$offset", item.Offset);
                proposal.Parameters.AddWithValue("$now", now);
                proposal.ExecuteNonQuery();
            }

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
            using var query = connection.CreateCommand("""
                SELECT proposal_id, source_id, item_id, revision,
                       source_digest, processor_key, value, evidence,
                       evidence_offset, status, correction
                FROM eligible_enrichment_proposals
                WHERE source_id = $source
                ORDER BY evidence_offset, proposal_id LIMIT $limit
                """);
            query.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            query.Parameters.AddWithValue("$limit", limit);
            using var reader = query.ExecuteReader();
            var result = new List<LocalTopicProposal>();
            while (reader.Read())
            {
                result.Add(new LocalTopicProposal(
                    Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                    Guid.Parse(reader.GetString(2)), reader.GetInt32(3),
                    reader.GetString(4), reader.GetString(5), reader.GetString(6),
                    reader.GetString(7), reader.GetInt32(8), reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10)));
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

    public string Outcome(Guid sourceId)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("A valid source identifier is required.", nameof(sourceId));
        }

        return database.Execute(connection =>
        {
            using var query = connection.CreateCommand("""
                SELECT s.status, s.source_kind, s.locator, j.state, d.outcome, r.outcome
                FROM sources s
                JOIN items i ON i.source_id = s.source_id
                JOIN item_versions v ON v.item_id = i.item_id
                LEFT JOIN processing_jobs j ON j.item_id = v.item_id
                    AND j.revision = v.revision AND j.processor_key = 'source-text-v1'
                LEFT JOIN text_derivatives d ON d.item_id = v.item_id
                    AND d.revision = v.revision AND d.processor_key = 'source-text-v1'
                    AND d.source_digest = v.digest
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
            using var reader = query.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != "active")
            {
                return "unavailable";
            }

            if (!reader.IsDBNull(5))
            {
                return reader.GetString(5);
            }

            if (!reader.IsDBNull(4) && reader.GetString(4) == "unsupported")
            {
                return "unsupported";
            }

            if (!reader.IsDBNull(3) && reader.GetString(3) is "failed" or "canceled")
            {
                return "failed";
            }

            var extension = Path.GetExtension(reader.GetString(2)).ToLowerInvariant();
            if (reader.GetString(1) != "file" ||
                extension is not (".md" or ".markdown"))
            {
                return "unsupported";
            }

            return "pending";
        });
    }
}
