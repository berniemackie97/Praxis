using System.Text.Json;
using Microsoft.Data.Sqlite;
using Praxis.Application.Text;
using Praxis.Application.Understanding;
using Praxis.Domain.Enrichment;
using Praxis.Domain.Originals;
using Praxis.Domain.Policy;
using Praxis.Domain.Processing;
using Praxis.Domain.Understanding;
using Praxis.Infrastructure.Persistence.Enrichment;
using Praxis.Infrastructure.Persistence.Finance;
using Praxis.Infrastructure.Persistence.Processing;

namespace Praxis.Infrastructure.Persistence.Understanding;

/// <summary>
/// Intake state per revision. The understand stage's summary, knowledge proposals, typed records
/// and ledger projection are written in one transaction with the job's completion, so a revision
/// is either fully understood or not at all, and a lost lease writes nothing.
/// </summary>
public sealed class SqliteUnderstandingRepository : IUnderstandingRepository
{
    private readonly CatalogDatabase database;
    private readonly TimeProvider clock;

    internal SqliteUnderstandingRepository(CatalogDatabase database, TimeProvider clock)
    {
        this.database = database;
        this.clock = clock;
    }

    public LeasedUnderstandingSource? ReadLeasedSource(ProcessingLease lease)
    {
        ProcessingJobKey.Validate(lease.ItemId, lease.Revision, lease.ProcessorKey);
        return database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
                SELECT s.source_id, s.source_kind, s.locator, v.digest, o.byte_length,
                       d.format, d.pages, d.title, d.truncated, d.status, d.document_type, d.language, d.gist, d.record_kinds, c.category
                FROM processing_jobs j
                JOIN item_versions v ON v.item_id = j.item_id AND v.revision = j.revision
                JOIN original_objects o ON o.digest = v.digest
                JOIN items i ON i.item_id = j.item_id
                JOIN sources s ON s.source_id = i.source_id
                LEFT JOIN intake_documents d ON d.item_id = j.item_id AND d.revision = j.revision
                LEFT JOIN source_categories c ON c.source_id = s.source_id
                WHERE j.item_id = $item AND j.revision = $revision
                  AND j.processor_key = $processor AND j.state = 'running'
                  AND j.lease_key = $lease AND j.lease_until_utc > $now
                  AND s.status = 'active'
                """);
            ProcessingJobParameters.BindLease(command, lease);
            command.Parameters.AddWithValue("$now", Now());
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            DocumentFacts facts = new(Text(reader, 5) ?? "text", reader.IsDBNull(6) ? null : reader.GetInt32(6), Text(reader, 7),
                !reader.IsDBNull(8) && reader.GetInt64(8) == 1);
            string? status = Text(reader, 9);
            DocumentProfile? profile = status is null or UnderstandingStatuses.Read
                ? null
                : new DocumentProfile(Text(reader, 14) ?? DataCategories.Unclassified, Text(reader, 10), Text(reader, 11), Text(reader, 12),
                    (Text(reader, 13) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).Where(RecordKinds.IsValid).ToArray());
            return new LeasedUnderstandingSource(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                new OriginalObject(reader.GetString(3), reader.GetInt64(4)), facts, profile);
        });
    }

    public bool CommitTriage(ProcessingLease lease, string digest, DocumentProfile profile, string triageTier)
    {
        ProcessingJobKey.Validate(lease.ItemId, lease.Revision, lease.ProcessorKey);
        string now = Now();
        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            if (!SqliteProcessingQueue.TryCompleteLeased(connection, transaction, lease, digest, now))
            {
                return false;
            }

            IntakeDocumentRows.Ensure(connection, transaction, lease.ItemId, lease.Revision, digest, now);
            connection.Run(transaction, """
                UPDATE intake_documents
                SET document_type = $type, language = $language, gist = $gist, record_kinds = $kinds, triage_tier = $tier,
                    status = CASE WHEN status = 'read' THEN 'triaged' ELSE status END, updated_utc = $now
                WHERE item_id = $item AND revision = $revision
                """, ("$type", Bound(profile.DocumentType, DocumentProfile.MaximumTypeLength)), ("$language", profile.Language),
                ("$gist", Bound(profile.Gist, DocumentProfile.MaximumGistLength)), ("$kinds", string.Join(',', profile.RecordKinds)),
                ("$tier", triageTier == "local" ? "local" : "hint"), ("$now", now),
                ("$item", lease.ItemId.ToString("D")), ("$revision", lease.Revision));
            transaction.Commit();
            return true;
        });
    }

    public bool CommitUnderstanding(ProcessingLease lease, LeasedText input, UnderstandingCommit commit)
    {
        ProcessingJobKey.Validate(lease.ItemId, lease.Revision, lease.ProcessorKey);
        if (!UnderstandingStatuses.All.Contains(commit.Status, StringComparer.Ordinal))
        {
            throw new ArgumentException("The understanding status is invalid.", nameof(commit));
        }

        string now = Now();
        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            if (!SqliteProcessingQueue.TryCompleteLeased(connection, transaction, lease, input.Digest, now))
            {
                return false;
            }

            IntakeDocumentRows.Ensure(connection, transaction, lease.ItemId, lease.Revision, input.Digest, now);
            WriteProposals(connection, transaction, lease, input, commit, now);
            WriteRecords(connection, transaction, lease, input.Content, commit.Records, now);
            connection.Run(transaction, "DELETE FROM financial_documents WHERE item_id = $item AND revision = $revision AND processor_key = $processor",
                ("$item", lease.ItemId.ToString("D")), ("$revision", lease.Revision), ("$processor", lease.ProcessorKey));
            if (commit.Ledger is { } ledger)
            {
                SqliteFinancialLedgerRepository.Write(connection, transaction, lease, input.Digest, ledger, commit.Annotations, commit.LedgerModel, commit.LedgerTier, now);
            }

            connection.Run(transaction, """
                UPDATE intake_documents
                SET summary = $summary, document_type = COALESCE($type, document_type), status = $status, reason = $reason,
                    tier = $tier, provider_id = $provider, model_name = $model, verified_items = $verified, rejected_items = $rejected,
                    input_tokens = $input, output_tokens = $output, updated_utc = $now
                WHERE item_id = $item AND revision = $revision
                """, ("$summary", Bound(commit.Summary, UnderstandingCommit.MaximumSummaryLength)),
                ("$type", Bound(commit.DocumentType, DocumentProfile.MaximumTypeLength)), ("$status", commit.Status), ("$reason", Code(commit.Reason)),
                ("$tier", commit.Tier), ("$provider", commit.ProviderId), ("$model", Bound(commit.Identity?.Model, 200)),
                ("$verified", commit.VerifiedItems), ("$rejected", commit.RejectedItems), ("$input", commit.InputTokens), ("$output", commit.OutputTokens),
                ("$now", now), ("$item", lease.ItemId.ToString("D")), ("$revision", lease.Revision));
            transaction.Commit();
            return true;
        });
    }

    public bool CloseUnderstanding(ProcessingLease lease, string digest, string status, string reason, int inputTokens, int outputTokens)
    {
        ProcessingJobKey.Validate(lease.ItemId, lease.Revision, lease.ProcessorKey);
        string now = Now();
        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            if (!SqliteProcessingQueue.TryCompleteLeased(connection, transaction, lease, digest, now))
            {
                return false;
            }

            IntakeDocumentRows.Ensure(connection, transaction, lease.ItemId, lease.Revision, digest, now);
            connection.Run(transaction, """
                UPDATE intake_documents
                SET status = $status, reason = $reason, input_tokens = input_tokens + $input, output_tokens = output_tokens + $output, updated_utc = $now
                WHERE item_id = $item AND revision = $revision
                """, ("$status", status), ("$reason", Code(reason)), ("$input", inputTokens), ("$output", outputTokens), ("$now", now),
                ("$item", lease.ItemId.ToString("D")), ("$revision", lease.Revision));
            transaction.Commit();
            return true;
        });
    }

    public int RequeueAwaitingModels()
    {
        string now = Now();
        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            int queued = connection.Run(transaction, """
                UPDATE processing_jobs
                SET state = 'queued', attempt = 0, lease_key = NULL, lease_until_utc = NULL, failure_code = NULL, updated_utc = $now
                WHERE processor_key = 'intake-understand-v1' AND state = 'completed'
                  AND revision = (SELECT MAX(latest.revision) FROM item_versions latest WHERE latest.item_id = processing_jobs.item_id)
                  AND EXISTS (SELECT 1 FROM items i JOIN sources s ON s.source_id = i.source_id
                              WHERE i.item_id = processing_jobs.item_id AND s.status = 'active')
                  AND EXISTS (SELECT 1 FROM intake_documents d
                              WHERE d.item_id = processing_jobs.item_id AND d.revision = processing_jobs.revision
                                AND d.status = 'index_only'
                                AND d.reason IN ('tier_unavailable', 'model_unavailable', 'model_timeout', 'model_quota_exhausted', 'model_unauthenticated')
                                AND d.updated_utc < (SELECT MAX(changed) FROM (
                                    SELECT updated_utc AS changed FROM model_tiers
                                    UNION ALL SELECT updated_utc FROM verification_providers WHERE enabled = 1)))
                """, ("$now", now));
            transaction.Commit();
            return queued;
        });
    }

    public bool TryReuse(ProcessingLease lease, LeasedText input)
    {
        ProcessingJobKey.Validate(lease.ItemId, lease.Revision, lease.ProcessorKey);
        string now = Now();
        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand find = connection.CreateCommand("""
                SELECT d.item_id, d.revision
                FROM intake_documents d
                JOIN processing_jobs j ON j.item_id = d.item_id AND j.revision = d.revision
                    AND j.processor_key = $processor AND j.state = 'completed'
                JOIN items i ON i.item_id = d.item_id
                JOIN sources s ON s.source_id = i.source_id
                WHERE d.source_digest = $digest AND d.status IN ('understood', 'partially_verified') AND s.status = 'active'
                  AND NOT (d.item_id = $item AND d.revision = $revision)
                ORDER BY d.updated_utc DESC LIMIT 1
                """);
            find.Transaction = transaction;
            find.Parameters.AddWithValue("$processor", lease.ProcessorKey);
            find.Parameters.AddWithValue("$digest", input.Digest);
            find.Parameters.AddWithValue("$item", lease.ItemId.ToString("D"));
            find.Parameters.AddWithValue("$revision", lease.Revision);
            string donorItem;
            int donorRevision;
            using (SqliteDataReader reader = find.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return false;
                }

                (donorItem, donorRevision) = (reader.GetString(0), reader.GetInt32(1));
            }

            if (!SqliteProcessingQueue.TryCompleteLeased(connection, transaction, lease, input.Digest, now))
            {
                return false;
            }

            IntakeDocumentRows.Ensure(connection, transaction, lease.ItemId, lease.Revision, input.Digest, now);
            (string Name, object? Value)[] keys =
            [
                ("$item", lease.ItemId.ToString("D")), ("$revision", lease.Revision), ("$processor", lease.ProcessorKey),
                ("$donor", donorItem), ("$donorRevision", donorRevision), ("$now", now)
            ];
            connection.Run(transaction, """
                UPDATE intake_documents
                SET (summary, document_type, language, gist, record_kinds, status, reason, provider_id, model_name, verified_items, rejected_items) =
                    (SELECT summary, document_type, language, gist, record_kinds, status, reason, provider_id, model_name, verified_items, rejected_items
                     FROM intake_documents WHERE item_id = $donor AND revision = $donorRevision),
                    tier = 'reused', input_tokens = 0, output_tokens = 0, updated_utc = $now
                WHERE item_id = $item AND revision = $revision
                """, keys);
            connection.Run(transaction, "DELETE FROM intake_records WHERE item_id = $item AND revision = $revision AND processor_key = $processor", keys);
            connection.Run(transaction, """
                INSERT INTO intake_records
                    (record_id, item_id, revision, processor_key, kind, fields, evidence, evidence_offset, evidence_length, sort_key, tier, provider_id, created_utc)
                SELECT lower(hex(randomblob(16))), $item, $revision, $processor, kind, fields, evidence, evidence_offset, evidence_length, sort_key, 'reused', provider_id, $now
                FROM intake_records WHERE item_id = $donor AND revision = $donorRevision AND processor_key = $processor
                """, keys);
            CopyLedger(connection, transaction, keys);
            List<ProposalCandidate> candidates = DonorCandidates(connection, transaction, donorItem, donorRevision, lease.ProcessorKey);
            if (candidates.Count > 0 && !HasRun(connection, transaction, lease))
            {
                SqliteProposalRepository.Write(connection, transaction, lease, input, candidates, null, now);
            }

            transaction.Commit();
            return true;
        });
    }

    public SourceUnderstanding? ReadCurrent(Guid sourceId) => database.ExecuteRead(connection =>
    {
        using SqliteCommand command = connection.CreateCommand("""
            SELECT d.item_id, d.revision, d.format, d.pages, d.title, d.truncated, c.category, d.document_type, d.language, d.gist, d.summary,
                   d.status, d.reason, d.tier, d.provider_id, d.model_name, d.verified_items, d.rejected_items,
                   (SELECT COUNT(*) FROM intake_records r WHERE r.item_id = d.item_id AND r.revision = d.revision),
                   (SELECT COUNT(*) FROM financial_transactions t WHERE t.item_id = d.item_id AND t.revision = d.revision),
                   d.input_tokens, d.output_tokens, d.updated_utc
            FROM items i
            JOIN sources s ON s.source_id = i.source_id
            JOIN intake_documents d ON d.item_id = i.item_id
            LEFT JOIN source_categories c ON c.source_id = s.source_id
            WHERE i.source_id = $source AND s.status = 'active'
              AND d.revision = (SELECT MAX(latest.revision) FROM item_versions latest WHERE latest.item_id = i.item_id)
            """);
        command.Parameters.AddWithValue("$source", sourceId.ToString("D"));
        using SqliteDataReader reader = command.ExecuteReader();
        return reader.Read()
            ? new SourceUnderstanding(Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetInt32(3),
                Text(reader, 4), reader.GetInt64(5) == 1, Text(reader, 6), Text(reader, 7), Text(reader, 8), Text(reader, 9), Text(reader, 10),
                reader.GetString(11), Text(reader, 12), Text(reader, 13), Text(reader, 14), Text(reader, 15), reader.GetInt32(16), reader.GetInt32(17),
                reader.GetInt32(18), reader.GetInt32(19), reader.GetInt64(20), reader.GetInt64(21), CatalogValues.ParseTimestamp(reader.GetString(22)))
            : null;
    });

    public IReadOnlyList<StoredRecord> ReadRecords(string? kind, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(limit, 20_000);
        return database.ExecuteRead(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
                SELECT s.source_id, r.item_id, r.revision, s.locator, r.kind, r.fields, r.evidence, r.evidence_offset, r.evidence_length, r.sort_key, r.tier
                FROM intake_records r
                JOIN items i ON i.item_id = r.item_id
                JOIN sources s ON s.source_id = i.source_id
                WHERE s.status = 'active' AND ($kind IS NULL OR r.kind = $kind)
                  AND r.revision = (SELECT MAX(latest.revision) FROM item_versions latest WHERE latest.item_id = r.item_id)
                ORDER BY r.sort_key IS NULL, r.sort_key DESC, r.record_id
                LIMIT $limit
                """);
            command.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
            command.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader reader = command.ExecuteReader();
            List<StoredRecord> records = [];
            while (reader.Read())
            {
                Dictionary<string, string> fields = JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(5)) ?? [];
                records.Add(new StoredRecord(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt32(2),
                    Path.GetFileName(reader.GetString(3)), reader.GetString(4), fields, reader.GetString(6), reader.GetInt32(7), reader.GetInt32(8),
                    Text(reader, 9), reader.GetString(10)));
            }

            return (IReadOnlyList<StoredRecord>)records;
        });
    }

    public string? ReadLayout(string fingerprint) => database.Execute(connection =>
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand read = connection.CreateCommand("SELECT mapping FROM intake_layouts WHERE fingerprint = $fingerprint");
        read.Transaction = transaction;
        read.Parameters.AddWithValue("$fingerprint", fingerprint);
        string? mapping = read.ExecuteScalar() as string;
        if (mapping is not null)
        {
            connection.Run(transaction, "UPDATE intake_layouts SET uses = uses + 1, used_utc = $now WHERE fingerprint = $fingerprint",
                ("$now", Now()), ("$fingerprint", fingerprint));
        }

        transaction.Commit();
        return mapping;
    });

    public void SaveLayout(string fingerprint, string mapping, string tier, string providerId) => database.Execute(connection =>
    {
        using SqliteTransaction transaction = connection.BeginTransaction();
        string now = Now();
        connection.Run(transaction, """
            INSERT INTO intake_layouts (fingerprint, mapping, tier, provider_id, uses, created_utc, used_utc)
            VALUES ($fingerprint, $mapping, $tier, $provider, 1, $now, $now)
            ON CONFLICT(fingerprint) DO UPDATE SET mapping = excluded.mapping, tier = excluded.tier, provider_id = excluded.provider_id, used_utc = excluded.used_utc
            """, ("$fingerprint", fingerprint), ("$mapping", mapping), ("$tier", tier), ("$provider", providerId), ("$now", now));
        transaction.Commit();
        return true;
    });

    /// <summary>
    /// Records knowledge proposals once per job: a rerun keeps proposals already recorded (and the
    /// owner's reviews of them) and only fills in a run that produced none.
    /// </summary>
    private static void WriteProposals(SqliteConnection connection, SqliteTransaction transaction, ProcessingLease lease, LeasedText input,
        UnderstandingCommit commit, string now)
    {
        if (commit.Candidates.Count == 0 || HasRun(connection, transaction, lease))
        {
            return;
        }

        connection.Run(transaction, "DELETE FROM enrichment_runs WHERE item_id = $item AND revision = $revision AND processor_key = $processor",
            ("$item", lease.ItemId.ToString("D")), ("$revision", lease.Revision), ("$processor", lease.ProcessorKey));
        ModelRunRecord? run = commit.Identity is { } identity
            ? new ModelRunRecord(identity, commit.InputTokens, commit.OutputTokens, TimeSpan.Zero, 0, 0, commit.RejectedItems, 0)
            : null;
        SqliteProposalRepository.Write(connection, transaction, lease, input, commit.Candidates, run, now);
    }

    private static bool HasRun(SqliteConnection connection, SqliteTransaction transaction, ProcessingLease lease)
    {
        using SqliteCommand command = connection.CreateCommand("""
            SELECT EXISTS (SELECT 1 FROM enrichment_proposals WHERE item_id = $item AND revision = $revision AND processor_key = $processor)
            """);
        command.Transaction = transaction;
        ProcessingJobParameters.Bind(command, lease.ItemId, lease.Revision, lease.ProcessorKey);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    private static void WriteRecords(SqliteConnection connection, SqliteTransaction transaction, ProcessingLease lease, string text,
        IReadOnlyList<IntakeRecord> records, string now)
    {
        connection.Run(transaction, "DELETE FROM intake_records WHERE item_id = $item AND revision = $revision AND processor_key = $processor",
            ("$item", lease.ItemId.ToString("D")), ("$revision", lease.Revision), ("$processor", lease.ProcessorKey));
        foreach (IntakeRecord record in records.Take(IntakeRecord.MaximumRecordsPerDocument))
        {
            if (record.EvidenceOffset < 0 || record.EvidenceLength is < 1 or > ProposalLimits.MaximumEvidenceLength ||
                record.EvidenceOffset > text.Length - record.EvidenceLength || record.Kind == RecordKinds.Transaction || !RecordKinds.IsValid(record.Kind))
            {
                throw new ArgumentException("A record has an invalid kind or evidence span.", nameof(records));
            }

            connection.Run(transaction, """
                INSERT INTO intake_records
                    (record_id, item_id, revision, processor_key, kind, fields, evidence, evidence_offset, evidence_length, sort_key, tier, provider_id, created_utc)
                VALUES ($id, $item, $revision, $processor, $kind, $fields, $evidence, $offset, $length, $sort, $tier, $provider, $now)
                """, ("$id", Guid.NewGuid().ToString("N")), ("$item", lease.ItemId.ToString("D")), ("$revision", lease.Revision),
                ("$processor", lease.ProcessorKey), ("$kind", record.Kind), ("$fields", JsonSerializer.Serialize(record.Fields)),
                ("$evidence", text.Substring(record.EvidenceOffset, record.EvidenceLength)), ("$offset", record.EvidenceOffset),
                ("$length", record.EvidenceLength), ("$sort", record.SortKey), ("$tier", record.Tier), ("$provider", record.ProviderId), ("$now", now));
        }
    }

    private static void CopyLedger(SqliteConnection connection, SqliteTransaction transaction, (string Name, object? Value)[] keys)
    {
        connection.Run(transaction, "DELETE FROM financial_documents WHERE item_id = $item AND revision = $revision AND processor_key = $processor", keys);
        connection.Run(transaction, """
            INSERT INTO financial_documents (item_id, revision, processor_key, source_digest, outcome, format, parser, reason_code, issues,
                tier, model_provider, model_name, model_digest, created_utc)
            SELECT $item, $revision, $processor, source_digest, outcome, format, parser, reason_code, issues, tier, model_provider, model_name, model_digest, $now
            FROM financial_documents WHERE item_id = $donor AND revision = $donorRevision AND processor_key = $processor
            """, keys);
        connection.Run(transaction, """
            INSERT INTO financial_accounts (item_id, revision, processor_key, account_key, kind, mask, institution, currency, display_name)
            SELECT $item, $revision, $processor, account_key, kind, mask, institution, currency, display_name
            FROM financial_accounts WHERE item_id = $donor AND revision = $donorRevision AND processor_key = $processor
            """, keys);
        connection.Run(transaction, """
            INSERT INTO financial_statements (item_id, revision, processor_key, ordinal, account_key, period_start, period_end,
                opening_minor, closing_minor, currency, verification, verification_note)
            SELECT $item, $revision, $processor, ordinal, account_key, period_start, period_end, opening_minor, closing_minor, currency, verification, verification_note
            FROM financial_statements WHERE item_id = $donor AND revision = $donorRevision AND processor_key = $processor
            """, keys);
        connection.Run(transaction, """
            INSERT INTO financial_transactions (item_id, revision, processor_key, ordinal, account_key, statement_ordinal, posted_on, amount_minor,
                currency, description, status, provider_type, provider_id, check_number, provider_category, balance_minor, locator, origin)
            SELECT $item, $revision, $processor, ordinal, account_key, statement_ordinal, posted_on, amount_minor, currency, description, status,
                provider_type, provider_id, check_number, provider_category, balance_minor, locator, origin
            FROM financial_transactions WHERE item_id = $donor AND revision = $donorRevision AND processor_key = $processor
            """, keys);
        connection.Run(transaction, """
            INSERT INTO financial_annotations (item_id, revision, records_processor, ordinal, merchant, category, tier, provider_id, model_name, created_utc)
            SELECT $item, $revision, $processor, ordinal, merchant, category, 'reused', provider_id, model_name, $now
            FROM financial_annotations WHERE item_id = $donor AND revision = $donorRevision AND records_processor = $processor
            """, keys);
    }

    private static List<ProposalCandidate> DonorCandidates(SqliteConnection connection, SqliteTransaction transaction, string item, int revision, string processor)
    {
        using SqliteCommand command = connection.CreateCommand("""
            SELECT kind, entity_type, value, evidence_offset, evidence_length, relation_subject, relation_object, flags
            FROM enrichment_proposals WHERE item_id = $item AND revision = $revision AND processor_key = $processor
            ORDER BY created_utc, proposal_id
            """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$item", item);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$processor", processor);
        List<ProposalCandidate> candidates = [];
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read() && candidates.Count < ProposalLimits.MaximumPerRun)
        {
            candidates.Add(new ProposalCandidate(reader.GetString(0), Text(reader, 1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4),
                Text(reader, 5), Text(reader, 6), Text(reader, 7)));
        }

        return candidates;
    }

    private string Now() => CatalogValues.Timestamp(clock.GetUtcNow());

    private static string? Text(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);

    private static string? Bound(string? value, int maximum) => value is null ? null : value.Length <= maximum ? value : value[..maximum];

    private static string? Code(string? reason) => reason is null ? null : reason.Length <= 64 ? reason : reason[..64];
}
