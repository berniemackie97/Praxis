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

/// <summary>
/// Durable, leased processing jobs keyed by immutable item revision and processor.
/// Leases fence stale workers; attempt budgets bound retries; only active sources are claimable.
/// </summary>
public sealed class LocalProcessingQueue
{
    private readonly CatalogDatabase database;

    internal LocalProcessingQueue(CatalogDatabase database) => this.database = database;

    public IReadOnlyList<ProcessingJob> List(int limit = 100)
    {
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return database.Execute(connection =>
        {
            using var query = connection.CreateCommand("""
                SELECT item_id, revision, processor_key, state, attempt, total_attempts,
                       lease_until_utc, failure_code
                FROM processing_jobs
                ORDER BY created_utc, item_id, revision, processor_key
                LIMIT $limit
                """);
            query.Parameters.AddWithValue("$limit", limit);
            using var reader = query.ExecuteReader();
            var jobs = new List<ProcessingJob>();
            while (reader.Read())
            {
                jobs.Add(new ProcessingJob(
                    Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : CatalogValues.ParseTimestamp(reader.GetString(6)),
                    reader.IsDBNull(7) ? null : reader.GetString(7)));
            }

            return jobs;
        });
    }

    public bool Queue(Guid itemId, int revision, string processorKey)
    {
        ProcessingJobKey.Validate(itemId, revision, processorKey);
        var now = CatalogValues.Timestamp(DateTimeOffset.UtcNow);
        return database.Execute(connection =>
        {
            using var command = connection.CreateCommand("""
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
            ProcessingJobKey.Bind(command, itemId, revision, processorKey);
            command.Parameters.AddWithValue("$now", now);
            return command.ExecuteNonQuery() != 0;
        });
    }

    public ProcessingLease? TryClaim(
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
            ProcessingJobKey.ValidateProcessor(processorKey);
        }

        var time = nowUtc ?? DateTimeOffset.UtcNow;
        var now = CatalogValues.Timestamp(time);
        var leaseUntil = time.ToUniversalTime().Add(leaseDuration);
        return database.Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            ExpireAttemptLimitedJobs(connection, transaction, now, maxAttempts, processorKey);
            using var select = connection.CreateCommand("""
                SELECT j.item_id, j.revision, j.processor_key
                FROM processing_jobs j
                JOIN items i ON i.item_id = j.item_id
                JOIN sources s ON s.source_id = i.source_id
                WHERE s.status = 'active'
                  AND ($processor IS NULL OR j.processor_key = $processor)
                  AND j.attempt < $maximum
                  AND (j.state = 'queued' OR
                       (j.state = 'running' AND j.lease_until_utc <= $now))
                ORDER BY j.priority DESC, j.created_utc, j.item_id, j.revision, j.processor_key
                LIMIT 1
                """);
            select.Transaction = transaction;
            select.Parameters.AddWithValue("$maximum", maxAttempts);
            select.Parameters.AddWithValue("$processor", (object?)processorKey ?? DBNull.Value);
            select.Parameters.AddWithValue("$now", now);
            Guid itemId;
            int revision;
            string processor;
            using (var reader = select.ExecuteReader())
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

            var lease = new ProcessingLease(itemId, revision, processor, Guid.NewGuid(), leaseUntil);
            using var claim = connection.CreateCommand("""
                UPDATE processing_jobs
                SET state = 'running', attempt = attempt + 1,
                    total_attempts = total_attempts + 1, lease_key = $lease,
                    lease_until_utc = $until, updated_utc = $now
                WHERE item_id = $item AND revision = $revision AND processor_key = $processor
                """);
            claim.Transaction = transaction;
            ProcessingJobKey.BindLease(claim, lease);
            claim.Parameters.AddWithValue("$until", CatalogValues.Timestamp(leaseUntil));
            claim.Parameters.AddWithValue("$now", now);
            if (claim.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("The processing claim could not be committed.");
            }

            transaction.Commit();
            return (ProcessingLease?)lease;
        });
    }

    public bool Finish(
        ProcessingLease lease, ProcessingCompletion completion,
        string? failureCode = null, DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ProcessingJobKey.Validate(lease.ItemId, lease.Revision, lease.ProcessorKey);
        if (lease.LeaseKey == Guid.Empty)
        {
            throw new ArgumentException("A processing lease key is required.", nameof(lease));
        }

        var state = completion switch
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

        var now = CatalogValues.Timestamp(nowUtc ?? DateTimeOffset.UtcNow);
        return database.Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand("""
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
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$failure", (object?)failureCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", now);
            ProcessingJobKey.BindLease(command, lease);
            if (command.ExecuteNonQuery() != 1)
            {
                return false;
            }

            if (completion == ProcessingCompletion.Completed)
            {
                QueueDependents(connection, transaction,
                    lease.ItemId, lease.Revision, lease.ProcessorKey, now);
            }

            transaction.Commit();
            return true;
        });
    }

    /// <summary>
    /// Extends a held lease for a long-running job. Returns null when the lease was lost or the
    /// source revoked. When a newer revision has been captured the job is canceled instead, so
    /// expensive work on superseded content stops at the next renewal.
    /// </summary>
    public ProcessingLease? Renew(
        ProcessingLease lease, TimeSpan extension, DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ProcessingJobKey.Validate(lease.ItemId, lease.Revision, lease.ProcessorKey);
        if (extension < TimeSpan.FromSeconds(1) || extension > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(extension));
        }

        var time = nowUtc ?? DateTimeOffset.UtcNow;
        var now = CatalogValues.Timestamp(time);
        var until = time.ToUniversalTime().Add(extension);
        return database.Execute(connection =>
        {
            using var transaction = connection.BeginTransaction();
            using var superseded = connection.CreateCommand("""
                UPDATE processing_jobs
                SET state = 'canceled', lease_key = NULL, lease_until_utc = NULL,
                    failure_code = NULL, updated_utc = $now
                WHERE item_id = $item AND revision = $revision AND processor_key = $processor
                  AND state = 'running' AND lease_key = $lease AND lease_until_utc > $now
                  AND EXISTS (SELECT 1 FROM item_versions newer
                              WHERE newer.item_id = processing_jobs.item_id
                                AND newer.revision > processing_jobs.revision)
                """);
            superseded.Transaction = transaction;
            ProcessingJobKey.BindLease(superseded, lease);
            superseded.Parameters.AddWithValue("$now", now);
            if (superseded.ExecuteNonQuery() == 1)
            {
                transaction.Commit();
                return null;
            }

            using var extend = connection.CreateCommand("""
                UPDATE processing_jobs
                SET lease_until_utc = $until, updated_utc = $now
                WHERE item_id = $item AND revision = $revision AND processor_key = $processor
                  AND state = 'running' AND lease_key = $lease AND lease_until_utc > $now
                  AND EXISTS (
                      SELECT 1 FROM items i JOIN sources s ON s.source_id = i.source_id
                      WHERE i.item_id = processing_jobs.item_id AND s.status = 'active')
                """);
            extend.Transaction = transaction;
            ProcessingJobKey.BindLease(extend, lease);
            extend.Parameters.AddWithValue("$until", CatalogValues.Timestamp(until));
            extend.Parameters.AddWithValue("$now", now);
            var renewed = extend.ExecuteNonQuery() == 1;
            transaction.Commit();
            return renewed ? lease with { LeaseUntil = until } : null;
        });
    }

    public bool Retry(Guid itemId, int revision, string processorKey)
    {
        ProcessingJobKey.Validate(itemId, revision, processorKey);
        return database.Execute(connection =>
        {
            using var command = connection.CreateCommand("""
                UPDATE processing_jobs
                SET state = 'queued', attempt = 0, failure_code = NULL,
                    lease_key = NULL, lease_until_utc = NULL, updated_utc = $now
                WHERE item_id = $item AND revision = $revision AND processor_key = $processor
                  AND state IN ('failed', 'canceled')
                  AND EXISTS (
                      SELECT 1 FROM items i JOIN sources s ON s.source_id = i.source_id
                      WHERE i.item_id = processing_jobs.item_id AND s.status = 'active')
                """);
            command.Parameters.AddWithValue("$now", CatalogValues.Timestamp(DateTimeOffset.UtcNow));
            ProcessingJobKey.Bind(command, itemId, revision, processorKey);
            return command.ExecuteNonQuery() == 1;
        });
    }

    public bool Cancel(Guid itemId, int revision, string processorKey)
    {
        ProcessingJobKey.Validate(itemId, revision, processorKey);
        return database.Execute(connection =>
        {
            using var command = connection.CreateCommand("""
                UPDATE processing_jobs
                SET state = 'canceled', failure_code = NULL, lease_key = NULL,
                    lease_until_utc = NULL, updated_utc = $now
                WHERE item_id = $item AND revision = $revision AND processor_key = $processor
                  AND state IN ('queued', 'running', 'failed')
                """);
            command.Parameters.AddWithValue("$now", CatalogValues.Timestamp(DateTimeOffset.UtcNow));
            ProcessingJobKey.Bind(command, itemId, revision, processorKey);
            return command.ExecuteNonQuery() == 1;
        });
    }

    /// <summary>Schedules a newly committed revision inside the caller's capture transaction.</summary>
    internal static void QueueVersion(
        SqliteConnection connection, SqliteTransaction transaction,
        Guid itemId, int revision, string processor, string utc)
    {
        using var command = connection.CreateCommand("""
            INSERT INTO processing_jobs
                (item_id, revision, processor_key, state, attempt, total_attempts,
                 created_utc, updated_utc)
            VALUES ($item, $revision, $processor, 'queued', 0, 0, $now, $now)
            """);
        command.Transaction = transaction;
        ProcessingJobKey.Bind(command, itemId, revision, processor);
        command.Parameters.AddWithValue("$now", utc);
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Completes a leased job inside the caller's result transaction, only while the lease is
    /// held, the source is active and the revision still carries <paramref name="digest"/>;
    /// then schedules dependents. Returns false, writing nothing, when any fence fails.
    /// </summary>
    internal static bool TryCompleteLeased(
        SqliteConnection connection, SqliteTransaction transaction,
        ProcessingLease lease, string digest, string utc)
    {
        using var finish = connection.CreateCommand("""
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
        finish.Parameters.AddWithValue("$digest", digest);
        finish.Parameters.AddWithValue("$now", utc);
        if (finish.ExecuteNonQuery() != 1)
        {
            return false;
        }

        QueueDependents(connection, transaction, lease.ItemId, lease.Revision, lease.ProcessorKey, utc);
        return true;
    }

    /// <summary>
    /// Schedules the processors that consume a just-completed stage, inside the transaction
    /// that records the completion. Existing jobs for the same revision are left untouched.
    /// </summary>
    internal static void QueueDependents(
        SqliteConnection connection, SqliteTransaction transaction,
        Guid itemId, int revision, string completedProcessor, string utc)
    {
        foreach (var dependent in LocalProcessors.DependentsOf(completedProcessor))
        {
            using var command = connection.CreateCommand("""
                INSERT INTO processing_jobs
                    (item_id, revision, processor_key, state, attempt, total_attempts,
                     created_utc, updated_utc)
                VALUES ($item, $revision, $processor, 'queued', 0, 0, $now, $now)
                ON CONFLICT(item_id, revision, processor_key) DO NOTHING
                """);
            command.Transaction = transaction;
            ProcessingJobKey.Bind(command, itemId, revision, dependent);
            command.Parameters.AddWithValue("$now", utc);
            command.ExecuteNonQuery();
        }
    }

    private static void ExpireAttemptLimitedJobs(
        SqliteConnection connection, SqliteTransaction transaction,
        string now, int maxAttempts, string? processorKey)
    {
        using var command = connection.CreateCommand("""
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
}
