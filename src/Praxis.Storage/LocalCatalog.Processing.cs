using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Praxis.Storage;

public enum ProcessingCompletion
{
    Completed,
    Unsupported,
    Failed
}

public sealed record ProcessingJob(
    Guid ItemId, int Revision, string ProcessorKey, string State,
    int Attempt, int TotalAttempts, DateTimeOffset? LeaseUntil, string? FailureCode);

public sealed record ProcessingLease(
    Guid ItemId, int Revision, string ProcessorKey, Guid LeaseKey, DateTimeOffset LeaseUntil);

public sealed partial class LocalCatalog
{
    public const string DefaultProcessorKey = "source-text-v1";

    public IReadOnlyList<ProcessingJob> ListProcessingJobs(int limit = 100)
    {
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        lock (connection)
        {
            using SqliteCommand query = Command(connection, """
                                                            SELECT item_id, revision, processor_key, state, attempt, total_attempts,
                                                                   lease_until_utc, failure_code
                                                            FROM processing_jobs
                                                            ORDER BY created_utc, item_id, revision, processor_key
                                                            LIMIT $limit
                                                            """);
            query.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader reader = query.ExecuteReader();
            List<ProcessingJob> jobs = new List<ProcessingJob>();
            while (reader.Read())
            {
                jobs.Add(new ProcessingJob(
                    Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6),
                        CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }

            return jobs;
        }
    }

    public bool QueueProcessing(Guid itemId, int revision, string processorKey)
    {
        ValidateJobIdentity(itemId, revision, processorKey);
        string now = Timestamp(DateTimeOffset.UtcNow);
        lock (connection)
        {
            using SqliteCommand command = Command(connection, """
                                                              INSERT INTO processing_jobs
                                                                  (item_id, revision, processor_key, state, attempt, total_attempts,
                                                                   created_utc, updated_utc)
                                                              SELECT v.item_id, v.revision, $processor, 'queued', 0, 0, $now, $now
                                                              FROM item_versions v
                                                              JOIN items i ON i.item_id = v.item_id
                                                              JOIN sources s ON s.source_id = i.source_id
                                                              WHERE v.item_id = $item AND v.revision = $revision AND s.status = 'active'
                                                              ON CONFLICT(item_id, revision, processor_key) DO NOTHING
                                                              """);
            command.Parameters.AddWithValue("$processor", processorKey);
            command.Parameters.AddWithValue("$item", itemId.ToString("D"));
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue("$now", now);
            return command.ExecuteNonQuery() != 0;
        }
    }

    public ProcessingLease? TryClaimProcessing(
        TimeSpan leaseDuration, int maxAttempts = 3, DateTimeOffset? nowUtc = null,
        string? processorKey = null)
    {
        if (leaseDuration < TimeSpan.FromSeconds(1) ||
            leaseDuration > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }

        if (maxAttempts is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        }

        if (processorKey is not null)
        {
            ValidateProcessorKey(processorKey);
        }

        DateTimeOffset time = nowUtc ?? DateTimeOffset.UtcNow;
        string now = Timestamp(time);
        DateTimeOffset leaseUntil = time.ToUniversalTime().Add(leaseDuration);
        lock (connection)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            ExpireAttemptLimitedJobs(now, maxAttempts, processorKey, transaction);
            using SqliteCommand select = Command(connection, """
                                                             SELECT j.item_id, j.revision, j.processor_key
                                                             FROM processing_jobs j
                                                             JOIN items i ON i.item_id = j.item_id
                                                             JOIN sources s ON s.source_id = i.source_id
                                                             WHERE s.status = 'active'
                                                               AND ($processor IS NULL OR j.processor_key = $processor)
                                                               AND j.attempt < $maximum
                                                               AND (j.state = 'queued' OR
                                                                    (j.state = 'running' AND j.lease_until_utc <= $now))
                                                             ORDER BY j.created_utc, j.item_id, j.revision, j.processor_key
                                                             LIMIT 1
                                                             """);
            select.Transaction = transaction;
            select.Parameters.AddWithValue("$maximum", maxAttempts);
            select.Parameters.AddWithValue("$processor", (object?)processorKey ?? DBNull.Value);
            select.Parameters.AddWithValue("$now", now);
            Guid itemId;
            int revision;
            string processor;
            using (SqliteDataReader reader = select.ExecuteReader())
            {
                if (!reader.Read())
                {
                    transaction.Commit();
                    return null;
                }

                itemId = Guid.Parse(reader.GetString(0));
                revision = reader.GetInt32(1);
                processor = reader.GetString(2);
            }

            Guid leaseKey = Guid.NewGuid();
            using SqliteCommand claim = Command(connection, """
                                                            UPDATE processing_jobs
                                                            SET state = 'running', attempt = attempt + 1,
                                                                total_attempts = total_attempts + 1, lease_key = $lease,
                                                                lease_until_utc = $until, updated_utc = $now
                                                            WHERE item_id = $item AND revision = $revision AND processor_key = $processor
                                                            """);
            claim.Transaction = transaction;
            claim.Parameters.AddWithValue("$lease", leaseKey.ToString("D"));
            claim.Parameters.AddWithValue("$until", Timestamp(leaseUntil));
            claim.Parameters.AddWithValue("$now", now);
            claim.Parameters.AddWithValue("$item", itemId.ToString("D"));
            claim.Parameters.AddWithValue("$revision", revision);
            claim.Parameters.AddWithValue("$processor", processor);
            if (claim.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("The processing claim could not be committed.");
            }

            transaction.Commit();
            return new ProcessingLease(itemId, revision, processor, leaseKey, leaseUntil);
        }
    }

    public bool FinishProcessing(
        ProcessingLease lease, ProcessingCompletion completion,
        string? failureCode = null, DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateJobIdentity(lease.ItemId, lease.Revision, lease.ProcessorKey);
        if (lease.LeaseKey == Guid.Empty)
        {
            throw new ArgumentException("A processing lease key is required.", nameof(lease));
        }

        string state = completion switch
        {
            ProcessingCompletion.Completed => "completed",
            ProcessingCompletion.Unsupported => "unsupported",
            ProcessingCompletion.Failed => "failed",
            _ => throw new ArgumentOutOfRangeException(nameof(completion))
        };
        if (completion == ProcessingCompletion.Failed)
        {
            if (string.IsNullOrEmpty(failureCode) || failureCode.Length > 64 ||
                failureCode.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_')))
            {
                throw new ArgumentException("Use a bounded non-sensitive failure code.", nameof(failureCode));
            }
        }
        else if (failureCode is not null)
        {
            throw new ArgumentException("Only failed processing accepts a failure code.", nameof(failureCode));
        }

        string now = Timestamp(nowUtc ?? DateTimeOffset.UtcNow);
        lock (connection)
        {
            using SqliteCommand command = Command(connection, """
                                                              UPDATE processing_jobs
                                                              SET state = $state, lease_key = NULL, lease_until_utc = NULL,
                                                                  failure_code = $failure, updated_utc = $now
                                                              WHERE item_id = $item AND revision = $revision
                                                                AND processor_key = $processor AND state = 'running'
                                                                AND lease_key = $lease AND lease_until_utc > $now
                                                                AND EXISTS (
                                                                    SELECT 1 FROM items i JOIN sources s ON s.source_id = i.source_id
                                                                    WHERE i.item_id = processing_jobs.item_id AND s.status = 'active')
                                                              """);
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$failure", (object?)failureCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", now);
            BindJob(command, lease.ItemId, lease.Revision, lease.ProcessorKey);
            command.Parameters.AddWithValue("$lease", lease.LeaseKey.ToString("D"));
            return command.ExecuteNonQuery() == 1;
        }
    }

    public bool RetryProcessing(Guid itemId, int revision, string processorKey)
    {
        ValidateJobIdentity(itemId, revision, processorKey);
        lock (connection)
        {
            using SqliteCommand command = Command(connection, """
                                                              UPDATE processing_jobs
                                                              SET state = 'queued', attempt = 0, failure_code = NULL,
                                                                  lease_key = NULL, lease_until_utc = NULL, updated_utc = $now
                                                              WHERE item_id = $item AND revision = $revision AND processor_key = $processor
                                                                AND state IN ('failed', 'canceled')
                                                                AND EXISTS (
                                                                    SELECT 1 FROM items i JOIN sources s ON s.source_id = i.source_id
                                                                    WHERE i.item_id = processing_jobs.item_id AND s.status = 'active')
                                                              """);
            command.Parameters.AddWithValue("$now", Timestamp(DateTimeOffset.UtcNow));
            BindJob(command, itemId, revision, processorKey);
            return command.ExecuteNonQuery() == 1;
        }
    }

    public bool CancelProcessing(Guid itemId, int revision, string processorKey)
    {
        ValidateJobIdentity(itemId, revision, processorKey);
        lock (connection)
        {
            using SqliteCommand command = Command(connection, """
                                                              UPDATE processing_jobs
                                                              SET state = 'canceled', failure_code = NULL, lease_key = NULL,
                                                                  lease_until_utc = NULL, updated_utc = $now
                                                              WHERE item_id = $item AND revision = $revision AND processor_key = $processor
                                                                AND state IN ('queued', 'running', 'failed')
                                                              """);
            command.Parameters.AddWithValue("$now", Timestamp(DateTimeOffset.UtcNow));
            BindJob(command, itemId, revision, processorKey);
            return command.ExecuteNonQuery() == 1;
        }
    }

    public bool RevokeSource(Guid sourceId)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("A source ID is required.", nameof(sourceId));
        }

        lock (connection)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand source = Command(connection, """
                                                             UPDATE sources SET status = 'revoked'
                                                             WHERE source_id = $source AND status = 'active'
                                                             """);
            source.Transaction = transaction;
            source.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            if (source.ExecuteNonQuery() == 0)
            {
                return false;
            }

            using SqliteCommand jobs = Command(connection, """
                                                           UPDATE processing_jobs
                                                           SET state = 'canceled', failure_code = NULL, lease_key = NULL,
                                                               lease_until_utc = NULL, updated_utc = $now
                                                           WHERE item_id IN (SELECT item_id FROM items WHERE source_id = $source)
                                                             AND state IN ('queued', 'running', 'failed')
                                                           """);
            jobs.Transaction = transaction;
            jobs.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            jobs.Parameters.AddWithValue("$now", Timestamp(DateTimeOffset.UtcNow));
            jobs.ExecuteNonQuery();
            transaction.Commit();
            return true;
        }
    }

    private void ExpireAttemptLimitedJobs(
        string now, int maxAttempts, string? processorKey, SqliteTransaction transaction)
    {
        using SqliteCommand command = Command(connection, """
                                                          UPDATE processing_jobs
                                                          SET state = 'failed', failure_code = 'attempt_limit',
                                                              lease_key = NULL, lease_until_utc = NULL, updated_utc = $now
                                                          WHERE attempt >= $maximum AND ($processor IS NULL OR processor_key = $processor)
                                                              AND (state = 'queued' OR (state = 'running' AND lease_until_utc <= $now))
                                                          """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$maximum", maxAttempts);
        command.Parameters.AddWithValue("$processor", (object?)processorKey ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    private void QueueProcessingVersion(
        Guid itemId, int revision, string processor, string utc,
        SqliteTransaction transaction)
    {
        using SqliteCommand command = Command(connection, """
                                                          INSERT INTO processing_jobs
                                                              (item_id, revision, processor_key, state, attempt, total_attempts,
                                                               created_utc, updated_utc)
                                                          VALUES ($item, $revision, $processor, 'queued', 0, 0, $now, $now)
                                                          """);
        command.Transaction = transaction;
        BindJob(command, itemId, revision, processor);
        command.Parameters.AddWithValue("$now", utc);
        command.ExecuteNonQuery();
    }

    private static void BindJob(SqliteCommand command, Guid itemId, int revision, string processor)
    {
        command.Parameters.AddWithValue("$item", itemId.ToString("D"));
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$processor", processor);
    }

    private static void ValidateJobIdentity(Guid itemId, int revision, string processor)
    {
        if (itemId == Guid.Empty || revision < 1)
        {
            throw new ArgumentException("A valid item and revision are required.");
        }

        ValidateProcessorKey(processor);
    }

    private static void ValidateProcessorKey(string processor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processor);
        if (processor.Length > 64 || processor.Any(c =>
            c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')))
        {
            throw new ArgumentException("Processor keys must be bounded lowercase identifiers.", nameof(processor));
        }
    }

    private static string Timestamp(DateTimeOffset time) =>
        time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
