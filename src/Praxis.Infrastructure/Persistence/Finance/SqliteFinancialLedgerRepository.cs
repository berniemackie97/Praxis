using System.Globalization;
using Microsoft.Data.Sqlite;
using Praxis.Application.Finance;
using Praxis.Domain.Finance;
using Praxis.Domain.Models;
using Praxis.Domain.Originals;
using Praxis.Domain.Processing;
using Praxis.Infrastructure.Persistence.Processing;

namespace Praxis.Infrastructure.Persistence.Finance;

/// <summary>
/// Typed financial facts bound to the revision digest that produced them. A result replaces any
/// earlier result for the same job, is committed atomically with the job's terminal state, and
/// is read only through <c>eligible_financial_documents</c> (active source, current revision,
/// completed job).
/// </summary>
public sealed class SqliteFinancialLedgerRepository : IFinancialLedgerRepository
{
    private const string Processor = ProcessorKeys.FinancialRecords;

    private readonly CatalogDatabase database;
    private readonly TimeProvider clock;

    internal SqliteFinancialLedgerRepository(CatalogDatabase database, TimeProvider clock)
    {
        this.database = database;
        this.clock = clock;
    }

    public LeasedFinancialSource? ReadLeasedSource(ProcessingLease lease)
    {
        ProcessingJobKey.Validate(lease.ItemId, lease.Revision, lease.ProcessorKey);
        return database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
                SELECT s.source_id, s.source_kind, s.locator, v.digest, o.byte_length
                FROM processing_jobs j
                JOIN item_versions v ON v.item_id = j.item_id AND v.revision = j.revision
                JOIN original_objects o ON o.digest = v.digest
                JOIN items i ON i.item_id = j.item_id
                JOIN sources s ON s.source_id = i.source_id
                WHERE j.item_id = $item AND j.revision = $revision
                  AND j.processor_key = $processor AND j.state = 'running'
                  AND j.lease_key = $lease AND j.lease_until_utc > $now
                  AND s.status = 'active'
                """);
            ProcessingJobParameters.BindLease(command, lease);
            command.Parameters.AddWithValue("$now", CatalogValues.Timestamp(clock.GetUtcNow()));
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read()
                ? new LeasedFinancialSource(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetString(2),
                    new OriginalObject(reader.GetString(3), reader.GetInt64(4)))
                : null;
        });
    }

    public bool Commit(ProcessingLease lease, FinancialDocument document, ModelIdentity? model, string? tier)
    {
        Validate(document);
        return Store(lease, "completed", (connection, transaction) =>
        {
            using SqliteCommand account = connection.CreateCommand("""
                INSERT INTO financial_accounts
                    (item_id, revision, processor_key, account_key, kind, mask, institution, currency, display_name)
                VALUES ($item, $revision, $processor, $key, $kind, $mask, $institution, $currency, $name)
                """);
            foreach (FinancialAccount value in document.Accounts)
            {
                Bind(account, transaction, lease, ("$key", value.AccountKey), ("$kind", value.Kind), ("$mask", value.Mask),
                    ("$institution", Truncate(value.Institution, 128)), ("$currency", value.Currency), ("$name", Truncate(value.DisplayName, 128)));
                account.ExecuteNonQuery();
            }

            using SqliteCommand statement = connection.CreateCommand("""
                INSERT INTO financial_statements
                    (item_id, revision, processor_key, ordinal, account_key, period_start, period_end,
                     opening_minor, closing_minor, currency, verification, verification_note)
                VALUES ($item, $revision, $processor, $ordinal, $key, $start, $end, $opening, $closing, $currency, $verification, $note)
                """);
            foreach (ParsedStatement value in document.Statements)
            {
                string currency = value.ClosingBalance?.Currency ?? value.OpeningBalance?.Currency ??
                                  document.Accounts.First(candidate => candidate.AccountKey == value.AccountKey).Currency;
                Bind(statement, transaction, lease, ("$ordinal", value.Ordinal), ("$key", value.AccountKey), ("$start", Day(value.PeriodStart)),
                    ("$end", Day(value.PeriodEnd)), ("$opening", value.OpeningBalance?.Minor), ("$closing", value.ClosingBalance?.Minor),
                    ("$currency", currency), ("$verification", value.Verification), ("$note", value.VerificationNote));
                statement.ExecuteNonQuery();
            }

            using SqliteCommand row = connection.CreateCommand("""
                INSERT INTO financial_transactions
                    (item_id, revision, processor_key, ordinal, account_key, statement_ordinal, posted_on, amount_minor, currency,
                     description, status, provider_type, provider_id, check_number, provider_category, balance_minor, locator, origin)
                VALUES ($item, $revision, $processor, $ordinal, $key, $statement, $posted, $amount, $currency,
                        $description, $status, $type, $provider, $check, $category, $balance, $locator, $origin)
                """);
            foreach (ParsedTransaction value in document.Transactions)
            {
                Bind(row, transaction, lease, ("$ordinal", value.Ordinal), ("$key", value.AccountKey), ("$statement", value.StatementOrdinal),
                    ("$posted", Day(value.PostedOn)), ("$amount", value.Amount.Minor), ("$currency", value.Amount.Currency),
                    ("$description", value.Description), ("$status", value.Status), ("$type", value.ProviderType),
                    ("$provider", value.ProviderId), ("$check", value.CheckNumber), ("$category", value.ProviderCategory),
                    ("$balance", value.BalanceAfter?.Minor), ("$locator", value.Locator), ("$origin", value.Origin));
                row.ExecuteNonQuery();
            }
        }, document.Format, document.Parser, null, string.Join(',', document.Issues.Take(32)), model, tier);
    }

    public bool CommitUnsupported(ProcessingLease lease, string reasonCode)
    {
        if (!ProcessingCodes.IsValid(reasonCode))
        {
            throw new ArgumentException("Use a bounded reason code.", nameof(reasonCode));
        }

        return Store(lease, "unsupported", (_, _) => { }, null, null, reasonCode, "", null, null);
    }

    public IReadOnlyList<StoredTransaction> ReadCountedTransactions() => database.ExecuteRead(connection =>
    {
        using SqliteCommand command = connection.CreateCommand("""
            SELECT t.ordinal, t.account_key, t.statement_ordinal, t.posted_on, t.amount_minor, t.currency, t.description, t.status,
                   t.provider_type, t.provider_id, t.check_number, t.provider_category, t.balance_minor, t.locator, t.origin,
                   a.kind, a.mask, a.institution, a.currency, a.display_name,
                   e.source_id, e.item_id, e.revision, e.source_digest, e.format, e.locator,
                   n.merchant, n.category, n.tier, n.provider_id, n.model_name
            FROM financial_transactions t
            JOIN eligible_financial_documents e ON e.item_id = t.item_id AND e.revision = t.revision AND e.processor_key = t.processor_key
            JOIN financial_accounts a ON a.item_id = t.item_id AND a.revision = t.revision
                AND a.processor_key = t.processor_key AND a.account_key = t.account_key
            LEFT JOIN financial_statements st ON st.item_id = t.item_id AND st.revision = t.revision
                AND st.processor_key = t.processor_key AND st.ordinal = t.statement_ordinal
            LEFT JOIN financial_annotations n ON n.item_id = t.item_id AND n.revision = t.revision AND n.ordinal = t.ordinal
            WHERE st.verification IS NULL OR st.verification <> 'unverified'
            ORDER BY t.item_id, t.revision, t.ordinal
            LIMIT $limit
            """);
        command.Parameters.AddWithValue("$limit", FinanceLimits.MaximumLedgerTransactions + 1);
        using SqliteDataReader reader = command.ExecuteReader();
        List<StoredTransaction> rows = [];
        while (reader.Read())
        {
            if (rows.Count == FinanceLimits.MaximumLedgerTransactions)
            {
                throw new InvalidOperationException("The financial ledger exceeds its supported size.");
            }

            string currency = reader.GetString(5);
            ParsedTransaction row = new(reader.GetInt32(0), reader.GetString(1), NullableInt(reader, 2), ParseDay(reader.GetString(3)),
                new MonetaryAmount(reader.GetInt64(4), currency), reader.GetString(6), reader.GetString(7), NullableText(reader, 8),
                NullableText(reader, 9), NullableText(reader, 10), NullableText(reader, 11),
                reader.IsDBNull(12) ? null : new MonetaryAmount(reader.GetInt64(12), currency), reader.GetString(13), reader.GetString(14));
            FinancialAccount account = new(row.AccountKey, reader.GetString(15), NullableText(reader, 16), NullableText(reader, 17),
                reader.GetString(18), reader.GetString(19));
            TransactionAnnotation? annotation = reader.IsDBNull(26) ? null
                : new TransactionAnnotation(row.Ordinal, reader.GetString(26), reader.GetString(27), reader.GetString(28), reader.GetString(29), NullableText(reader, 30));
            rows.Add(new StoredTransaction(row, account, Evidence(reader, 20, row.Locator), annotation));
        }

        return rows;
    });

    public IReadOnlyList<StoredStatement> ReadCountedStatements() => database.ExecuteRead(connection =>
    {
        using SqliteCommand command = connection.CreateCommand("""
            SELECT st.ordinal, st.account_key, st.period_start, st.period_end, st.opening_minor, st.closing_minor, st.currency,
                   st.verification, st.verification_note,
                   a.kind, a.mask, a.institution, a.currency, a.display_name,
                   e.source_id, e.item_id, e.revision, e.source_digest, e.format, e.locator
            FROM financial_statements st
            JOIN eligible_financial_documents e ON e.item_id = st.item_id AND e.revision = st.revision AND e.processor_key = st.processor_key
            JOIN financial_accounts a ON a.item_id = st.item_id AND a.revision = st.revision
                AND a.processor_key = st.processor_key AND a.account_key = st.account_key
            WHERE st.verification <> 'unverified' AND st.closing_minor IS NOT NULL
            ORDER BY st.item_id, st.revision, st.ordinal
            LIMIT 10000
            """);
        using SqliteDataReader reader = command.ExecuteReader();
        List<StoredStatement> statements = [];
        while (reader.Read())
        {
            string currency = reader.GetString(6);
            ParsedStatement statement = new(reader.GetInt32(0), reader.GetString(1), NullableDay(reader, 2), NullableDay(reader, 3),
                reader.IsDBNull(4) ? null : new MonetaryAmount(reader.GetInt64(4), currency),
                reader.IsDBNull(5) ? null : new MonetaryAmount(reader.GetInt64(5), currency), reader.GetString(7), NullableText(reader, 8));
            FinancialAccount account = new(statement.AccountKey, reader.GetString(9), NullableText(reader, 10), NullableText(reader, 11),
                reader.GetString(12), reader.GetString(13));
            statements.Add(new StoredStatement(statement, account, Evidence(reader, 14, $"statement:{statement.Ordinal + 1}")));
        }

        return statements;
    });

    public IReadOnlyList<FinancialAccount> ReadAccounts() => database.ExecuteRead(connection =>
    {
        using SqliteCommand command = connection.CreateCommand("""
            SELECT a.account_key, a.kind, a.mask, a.institution, a.currency, a.display_name
            FROM financial_accounts a
            JOIN eligible_financial_documents e ON e.item_id = a.item_id AND e.revision = a.revision AND e.processor_key = a.processor_key
            ORDER BY a.account_key, a.item_id
            LIMIT 10000
            """);
        using SqliteDataReader reader = command.ExecuteReader();
        List<FinancialAccount> accounts = [];
        while (reader.Read())
        {
            accounts.Add(new FinancialAccount(reader.GetString(0), reader.GetString(1), NullableText(reader, 2), NullableText(reader, 3),
                reader.GetString(4), reader.GetString(5)));
        }

        return accounts;
    });

    public IReadOnlyList<FinancialDocumentSummary> ReadDocuments(int limit)
    {
        if (limit is < 1 or > FinanceLimits.MaximumQueryResults)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return database.ExecuteRead(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
                SELECT s.source_id, d.item_id, d.revision, s.locator, d.outcome, d.format, d.reason_code, d.issues, d.model_name, d.created_utc,
                       (SELECT COUNT(*) FROM financial_accounts a WHERE a.item_id = d.item_id AND a.revision = d.revision AND a.processor_key = d.processor_key),
                       (SELECT COUNT(*) FROM financial_statements st WHERE st.item_id = d.item_id AND st.revision = d.revision AND st.processor_key = d.processor_key),
                       (SELECT COUNT(*) FROM financial_transactions t WHERE t.item_id = d.item_id AND t.revision = d.revision AND t.processor_key = d.processor_key),
                       (SELECT COUNT(*) FROM financial_statements st WHERE st.item_id = d.item_id AND st.revision = d.revision
                            AND st.processor_key = d.processor_key AND st.verification = 'unverified')
                FROM financial_documents d
                JOIN processing_jobs j ON j.item_id = d.item_id AND j.revision = d.revision
                    AND j.processor_key = d.processor_key AND j.state = d.outcome
                JOIN items i ON i.item_id = d.item_id
                JOIN sources s ON s.source_id = i.source_id
                WHERE s.status = 'active'
                  AND d.revision = (SELECT MAX(v.revision) FROM item_versions v WHERE v.item_id = d.item_id)
                  AND (d.outcome = 'completed' OR d.reason_code NOT IN ('not_financial', 'unsupported_source'))
                ORDER BY d.created_utc DESC, d.item_id
                LIMIT $limit
                """);
            command.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader reader = command.ExecuteReader();
            List<FinancialDocumentSummary> documents = [];
            while (reader.Read())
            {
                string issues = reader.GetString(7);
                documents.Add(new FinancialDocumentSummary(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), reader.GetInt32(2),
                    Path.GetFileName(reader.GetString(3)), reader.GetString(4), NullableText(reader, 5), NullableText(reader, 6),
                    reader.GetInt32(10), reader.GetInt32(11), reader.GetInt32(12), reader.GetInt32(13),
                    issues.Length == 0 ? [] : issues.Split(','), NullableText(reader, 8), CatalogValues.ParseTimestamp(reader.GetString(9))));
            }

            return documents;
        });
    }

    public LeasedFinancialRows? ReadLeasedTransactions(ProcessingLease lease)
    {
        ProcessingJobKey.Validate(lease.ItemId, lease.Revision, lease.ProcessorKey);
        string now = CatalogValues.Timestamp(clock.GetUtcNow());
        return database.Execute(connection =>
        {
            using SqliteCommand held = connection.CreateCommand("""
                SELECT e.source_id
                FROM processing_jobs j
                JOIN eligible_financial_documents e ON e.item_id = j.item_id AND e.revision = j.revision AND e.processor_key = $records
                WHERE j.item_id = $item AND j.revision = $revision AND j.processor_key = $processor AND j.state = 'running'
                  AND j.lease_key = $lease AND j.lease_until_utc > $now
                """);
            ProcessingJobParameters.BindLease(held, lease);
            held.Parameters.AddWithValue("$records", Processor);
            held.Parameters.AddWithValue("$now", now);
            if (held.ExecuteScalar() is not string sourceId)
            {
                return null;
            }

            using SqliteCommand rows = connection.CreateCommand("""
                SELECT t.ordinal, t.account_key, t.statement_ordinal, t.posted_on, t.amount_minor, t.currency, t.description, t.status
                FROM financial_transactions t
                LEFT JOIN financial_statements st ON st.item_id = t.item_id AND st.revision = t.revision
                    AND st.processor_key = t.processor_key AND st.ordinal = t.statement_ordinal
                WHERE t.item_id = $item AND t.revision = $revision AND t.processor_key = $records
                  AND (st.verification IS NULL OR st.verification <> 'unverified')
                ORDER BY t.ordinal
                """);
            rows.Parameters.AddWithValue("$item", lease.ItemId.ToString("D"));
            rows.Parameters.AddWithValue("$revision", lease.Revision);
            rows.Parameters.AddWithValue("$records", Processor);
            using SqliteDataReader reader = rows.ExecuteReader();
            List<ParsedTransaction> result = [];
            while (reader.Read())
            {
                result.Add(new ParsedTransaction(reader.GetInt32(0), reader.GetString(1), NullableInt(reader, 2), ParseDay(reader.GetString(3)),
                    new MonetaryAmount(reader.GetInt64(4), reader.GetString(5)), reader.GetString(6), reader.GetString(7),
                    null, null, null, null, null, $"ordinal:{reader.GetInt32(0)}", TransactionOrigins.Parser));
            }

            return new LeasedFinancialRows(Guid.Parse(sourceId), result);
        });
    }

    public bool CommitAnnotations(ProcessingLease lease, IReadOnlyList<TransactionAnnotation> annotations)
    {
        ProcessingJobKey.Validate(lease.ItemId, lease.Revision, lease.ProcessorKey);
        if (annotations.Any(item => item.Merchant.Length is < 1 or > 128 || !SpendingCategories.All.Contains(item.Category, StringComparer.Ordinal) ||
                                    !ModelTiers.All.Contains(item.Tier, StringComparer.Ordinal) || item.ProviderId.Length is < 1 or > 64))
        {
            throw new ArgumentException("An annotation is invalid.", nameof(annotations));
        }

        string now = CatalogValues.Timestamp(clock.GetUtcNow());
        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            int finished = connection.Run(transaction, """
                UPDATE processing_jobs
                SET state = 'completed', lease_key = NULL, lease_until_utc = NULL, failure_code = NULL, updated_utc = $now
                WHERE item_id = $item AND revision = $revision AND processor_key = $processor
                  AND state = 'running' AND lease_key = $lease AND lease_until_utc > $now
                  AND EXISTS (SELECT 1 FROM eligible_financial_documents e
                              WHERE e.item_id = $item AND e.revision = $revision AND e.processor_key = $records)
                """, ("$now", now), ("$item", lease.ItemId.ToString("D")), ("$revision", lease.Revision),
                ("$processor", lease.ProcessorKey), ("$lease", lease.LeaseKey.ToString("D")), ("$records", Processor));
            if (finished != 1)
            {
                return false;
            }

            connection.Run(transaction, "DELETE FROM financial_annotations WHERE item_id = $item AND revision = $revision",
                ("$item", lease.ItemId.ToString("D")), ("$revision", lease.Revision));
            using SqliteCommand insert = connection.CreateCommand("""
                INSERT OR IGNORE INTO financial_annotations
                    (item_id, revision, records_processor, ordinal, merchant, category, tier, provider_id, model_name, created_utc)
                SELECT $item, $revision, $records, $ordinal, $merchant, $category, $tier, $provider, $model, $now
                WHERE EXISTS (SELECT 1 FROM financial_transactions t
                              WHERE t.item_id = $item AND t.revision = $revision AND t.processor_key = $records AND t.ordinal = $ordinal)
                """);
            insert.Transaction = transaction;
            foreach (TransactionAnnotation item in annotations)
            {
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("$item", lease.ItemId.ToString("D"));
                insert.Parameters.AddWithValue("$revision", lease.Revision);
                insert.Parameters.AddWithValue("$records", Processor);
                insert.Parameters.AddWithValue("$ordinal", item.Ordinal);
                insert.Parameters.AddWithValue("$merchant", item.Merchant);
                insert.Parameters.AddWithValue("$category", item.Category);
                insert.Parameters.AddWithValue("$tier", item.Tier);
                insert.Parameters.AddWithValue("$provider", item.ProviderId);
                insert.Parameters.AddWithValue("$model", (object?)Truncate(item.ModelName, 200) ?? DBNull.Value);
                insert.Parameters.AddWithValue("$now", now);
                insert.ExecuteNonQuery();
            }

            transaction.Commit();
            return true;
        });
    }

    /// <summary>
    /// Replaces the job's result and closes the job in one transaction, only while the lease is
    /// held and the source is active. Returns false, writing nothing, when the lease was lost.
    /// </summary>
    private bool Store(ProcessingLease lease, string outcome, Action<SqliteConnection, SqliteTransaction> writeRows, string? format, string? parser,
        string? reasonCode, string issues, ModelIdentity? model, string? tier)
    {
        ProcessingJobKey.Validate(lease.ItemId, lease.Revision, lease.ProcessorKey);
        string now = CatalogValues.Timestamp(clock.GetUtcNow());
        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand source = connection.CreateCommand("""
                SELECT v.digest
                FROM processing_jobs j
                JOIN item_versions v ON v.item_id = j.item_id AND v.revision = j.revision
                JOIN items i ON i.item_id = j.item_id
                JOIN sources s ON s.source_id = i.source_id
                WHERE j.item_id = $item AND j.revision = $revision
                  AND j.processor_key = $processor AND j.state = 'running'
                  AND j.lease_key = $lease AND j.lease_until_utc > $now
                  AND s.status = 'active'
                """);
            source.Transaction = transaction;
            ProcessingJobParameters.BindLease(source, lease);
            source.Parameters.AddWithValue("$now", now);
            if (source.ExecuteScalar() is not string digest)
            {
                return false;
            }

            connection.Run(transaction, """
                DELETE FROM financial_documents WHERE item_id = $item AND revision = $revision AND processor_key = $processor
                """, ("$item", lease.ItemId.ToString("D")), ("$revision", lease.Revision), ("$processor", lease.ProcessorKey));
            connection.Run(transaction, """
                INSERT INTO financial_documents
                    (item_id, revision, processor_key, source_digest, outcome, format, parser, reason_code, issues,
                     tier, model_provider, model_name, model_digest, created_utc)
                VALUES ($item, $revision, $processor, $digest, $outcome, $format, $parser, $reason, $issues,
                        $attempted, $provider, $model, $modelDigest, $now)
                """, ("$item", lease.ItemId.ToString("D")), ("$revision", lease.Revision), ("$processor", lease.ProcessorKey),
                ("$digest", digest), ("$outcome", outcome), ("$format", format), ("$parser", parser), ("$reason", reasonCode),
                ("$issues", issues.Length > 1024 ? issues[..1024] : issues), ("$attempted", tier),
                ("$provider", model?.Provider), ("$model", Truncate(model?.Model, 200)), ("$modelDigest", Truncate(model?.Digest, 256)),
                ("$now", now));

            writeRows(connection, transaction);

            int finished = connection.Run(transaction, """
                UPDATE processing_jobs
                SET state = $outcome, lease_key = NULL, lease_until_utc = NULL, failure_code = NULL, updated_utc = $now
                WHERE item_id = $item AND revision = $revision AND processor_key = $processor
                  AND state = 'running' AND lease_key = $lease AND lease_until_utc > $now
                """, ("$outcome", outcome), ("$now", now), ("$item", lease.ItemId.ToString("D")), ("$revision", lease.Revision),
                ("$processor", lease.ProcessorKey), ("$lease", lease.LeaseKey.ToString("D")));
            if (finished != 1)
            {
                throw new InvalidOperationException("The financial result lost its lease.");
            }

            if (outcome == "completed")
            {
                // (Re)schedule understanding of the rows just recorded; a rerun replaces older annotations.
                connection.Run(transaction, """
                    INSERT INTO processing_jobs (item_id, revision, processor_key, state, attempt, total_attempts, created_utc, updated_utc)
                    VALUES ($item, $revision, $annotations, 'queued', 0, 0, $now, $now)
                    ON CONFLICT(item_id, revision, processor_key) DO UPDATE
                    SET state = 'queued', attempt = 0, lease_key = NULL, lease_until_utc = NULL, failure_code = NULL, updated_utc = $now
                    WHERE state IN ('completed', 'unsupported', 'failed', 'canceled')
                    """, ("$item", lease.ItemId.ToString("D")), ("$revision", lease.Revision),
                    ("$annotations", ProcessorKeys.FinancialCategories), ("$now", now));
            }

            transaction.Commit();
            return true;
        });
    }

    private static void Bind(SqliteCommand command, SqliteTransaction transaction, ProcessingLease lease, params (string Name, object? Value)[] values)
    {
        command.Transaction = transaction;
        command.Parameters.Clear();
        ProcessingJobParameters.Bind(command, lease.ItemId, lease.Revision, lease.ProcessorKey);
        foreach ((string name, object? value) in values)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private static void Validate(FinancialDocument document)
    {
        if (!FinancialFormats.All.Contains(document.Format, StringComparer.Ordinal) || document.Parser.Length is < 1 or > 64 ||
            document.Accounts.Count == 0 || document.Statements.Count > FinanceLimits.MaximumStatementsPerDocument ||
            document.Transactions.Count > FinanceLimits.MaximumTransactionsPerDocument)
        {
            throw new ArgumentException("The financial document is outside its supported bounds.", nameof(document));
        }

        HashSet<string> accounts = document.Accounts.Select(account => account.AccountKey).ToHashSet(StringComparer.Ordinal);
        HashSet<int> statements = document.Statements.Select(statement => statement.Ordinal).ToHashSet();
        if (accounts.Count != document.Accounts.Count || statements.Count != document.Statements.Count ||
            document.Accounts.Any(account => !AccountKinds.All.Contains(account.Kind, StringComparer.Ordinal) || !CurrencyExponents.IsValidCode(account.Currency)) ||
            document.Statements.Any(statement => !accounts.Contains(statement.AccountKey) || !StatementVerifications.All.Contains(statement.Verification, StringComparer.Ordinal)) ||
            document.Transactions.Any(row => !accounts.Contains(row.AccountKey) || (row.StatementOrdinal is { } ordinal && !statements.Contains(ordinal)) ||
                                             row.Description.Length is < 1 or > FinanceLimits.MaximumDescriptionLength ||
                                             row.Locator.Length is < 1 or > FinanceLimits.MaximumLocatorLength ||
                                             !CurrencyExponents.IsValidCode(row.Amount.Currency)) ||
            document.Issues.Any(issue => !ProcessingCodes.IsValid(issue)))
        {
            throw new ArgumentException("The financial document is inconsistent.", nameof(document));
        }
    }

    private static FinancialEvidence Evidence(SqliteDataReader reader, int start, string locator) =>
        new(Guid.Parse(reader.GetString(start)), Guid.Parse(reader.GetString(start + 1)), reader.GetInt32(start + 2), reader.GetString(start + 3),
            locator, reader.GetString(start + 4), Path.GetFileName(reader.GetString(start + 5)));

    private static string? Day(DateOnly? date) => date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly ParseDay(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static DateOnly? NullableDay(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : ParseDay(reader.GetString(column));

    private static string? NullableText(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetString(column);

    private static int? NullableInt(SqliteDataReader reader, int column) => reader.IsDBNull(column) ? null : reader.GetInt32(column);

    private static string? Truncate(string? text, int limit) => text is null || text.Length <= limit ? text : text[..limit];
}
