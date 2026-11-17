using Microsoft.Data.Sqlite;
using Praxis.Application.Processing;
using Praxis.Domain.Processing;

namespace Praxis.Infrastructure.Persistence.Processing;

/// <summary>
/// Durable, leased processing jobs keyed by immutable item revision and processor.
/// Leases fence stale workers; attempt budgets bound retries; only active sources are claimable.
/// </summary>
public sealed class SqliteProcessingQueue : IProcessingQueue
{
    private readonly CatalogDatabase database;

    private readonly TimeProvider clock;

    internal SqliteProcessingQueue(CatalogDatabase database, TimeProvider clock)
    {
        this.database = database;
        this.clock = clock;
    }

    public ProcessingBacklog Backlog() => database.ExecuteRead(connection =>
    {
        using SqliteCommand command = connection.CreateCommand("""
                                                                SELECT
                                                                    COALESCE(SUM(CASE WHEN j.state = 'queued' AND j.processor_key <> $enrichment THEN 1 ELSE 0 END), 0),
                                                                    COALESCE(SUM(CASE WHEN j.state = 'running' AND j.processor_key <> $enrichment THEN 1 ELSE 0 END), 0),
                                                                    COALESCE(SUM(CASE WHEN j.state = 'failed' AND j.processor_key <> $enrichment THEN 1 ELSE 0 END), 0),
                                                                    COALESCE(SUM(CASE WHEN j.state IN ('queued', 'running') AND j.processor_key = $enrichment
                                                                        AND EXISTS (SELECT 1 FROM model_profiles p WHERE p.role = 'local-enrichment' AND p.enabled = 1)
                                                                        THEN 1 ELSE 0 END), 0)
                                                                FROM processing_jobs j
                                                                JOIN items i ON i.item_id = j.item_id
                                                                JOIN sources s ON s.source_id = i.source_id
                                                                WHERE s.status = 'active'
                                                                  AND j.state IN ('queued', 'running', 'failed')
                                                                  AND j.revision = (
                                                                      SELECT MAX(v.revision)
                                                                      FROM item_versions v WHERE v.item_id = j.item_id)
                                                                """);
        // Optional local enrichment is reported apart from intake, which it never blocks.
        command.Parameters.AddWithValue("$enrichment", ProcessorKeys.SemanticExtraction);
        using SqliteDataReader reader = command.ExecuteReader();
        reader.Read();
        return new ProcessingBacklog(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3));
    });

    public IReadOnlyList<ProcessingJob> List(int limit = 100)
    {
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return database.ExecuteRead(connection =>
        {
            using SqliteCommand query = connection.CreateCommand("""
                                                                 SELECT item_id, revision, processor_key, state, attempt, total_attempts,
                                                                        lease_until_utc, failure_code
                                                                 FROM processing_jobs
                                                                 ORDER BY created_utc, item_id, revision, processor_key
                                                                 LIMIT $limit
                                                                 """);
            query.Parameters.AddWithValue("$limit", limit);
            using SqliteDataReader reader = query.ExecuteReader();
            List<ProcessingJob> jobs = new();
            while (reader.Read())
            {
                jobs.Add(new ProcessingJob(Guid.Parse(reader.GetString(0)), reader.GetInt32(1), reader.GetString(2),
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
        string now = CatalogValues.Timestamp(clock.GetUtcNow());
        return database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
                                                                   INSERT INTO processing_jobs
                                                                       (item_id, revision, processor_key, state, attempt, total_attempts,
                                                                        created_utc, updated_utc)
                                                                   SELECT v.item_id, v.revision, $processor, 'queued', 0, 0, $now, $now
                                                                   FROM item_versions v
                                                                   JOIN items i ON i.item_id = v.item_id
                                                                   JOIN sources s ON s.source_id = i.source_id
                                                                   WHERE v.item_id = $item AND v.revision = $revision AND s.status = 'active'
                                                                     AND v.revision = (
                                                                         SELECT MAX(latest.revision) FROM item_versions latest
                                                                         WHERE latest.item_id = v.item_id)
                                                                   ON CONFLICT(item_id, revision, processor_key) DO NOTHING
                                                                   """);
            ProcessingJobParameters.Bind(command, itemId, revision, processorKey);
            command.Parameters.AddWithValue("$now", now);
            return command.ExecuteNonQuery() != 0;
        });
    }

    public bool Requeue(Guid itemId, int revision, string processorKey)
    {
        ProcessingJobKey.Validate(itemId, revision, processorKey);
        string now = CatalogValues.Timestamp(clock.GetUtcNow());
        return database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
                INSERT INTO processing_jobs (item_id, revision, processor_key, state, attempt, total_attempts, created_utc, updated_utc)
                SELECT v.item_id, v.revision, $processor, 'queued', 0, 0, $now, $now
                FROM item_versions v
                JOIN items i ON i.item_id = v.item_id
                JOIN sources s ON s.source_id = i.source_id
                WHERE v.item_id = $item AND v.revision = $revision AND s.status = 'active'
                  AND v.revision = (SELECT MAX(latest.revision) FROM item_versions latest WHERE latest.item_id = v.item_id)
                ON CONFLICT(item_id, revision, processor_key) DO UPDATE
                SET state = 'queued', attempt = 0, lease_key = NULL, lease_until_utc = NULL, failure_code = NULL, updated_utc = $now
                WHERE processing_jobs.state IN ('completed', 'unsupported', 'failed', 'canceled')
                """);
            ProcessingJobParameters.Bind(command, itemId, revision, processorKey);
            command.Parameters.AddWithValue("$now", now);
            return command.ExecuteNonQuery() != 0;
        });
    }

    public ProcessingLease? TryClaim(TimeSpan leaseDuration, int maxAttempts = 3, string? processorKey = null)
    {
        if (leaseDuration < TimeSpan.FromSeconds(1) || leaseDuration > TimeSpan.FromHours(1))
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

        DateTimeOffset time = clock.GetUtcNow();
        string now = CatalogValues.Timestamp(time);
        DateTimeOffset leaseUntil = time.ToUniversalTime().Add(leaseDuration);
        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            ExpireAttemptLimitedJobs(connection, transaction, now, maxAttempts, processorKey);
            using SqliteCommand select = connection.CreateCommand($"""
                                                                  SELECT j.item_id, j.revision, j.processor_key
                                                                  FROM processing_jobs j
                                                                  JOIN items i ON i.item_id = j.item_id
                                                                  JOIN sources s ON s.source_id = i.source_id
                                                                  WHERE s.status = 'active' AND ({SqliteProcessingControls.Claimable})
                                                                    AND j.revision = (
                                                                        SELECT MAX(latest.revision) FROM item_versions latest
                                                                        WHERE latest.item_id = j.item_id)
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

            ProcessingLease lease = new(itemId, revision, processor, Guid.NewGuid(), leaseUntil);
            using SqliteCommand claim = connection.CreateCommand("""
                                                                 UPDATE processing_jobs
                                                                 SET state = 'running', attempt = attempt + 1,
                                                                     total_attempts = total_attempts + 1, lease_key = $lease,
                                                                     lease_until_utc = $until, updated_utc = $now
                                                                 WHERE item_id = $item AND revision = $revision AND processor_key = $processor
                                                                 """);
            claim.Transaction = transaction;
            ProcessingJobParameters.BindLease(claim, lease);
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

    public bool Finish(ProcessingLease lease, ProcessingCompletion completion, string? failureCode = null)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ProcessingJobKey.Validate(lease.ItemId, lease.Revision, lease.ProcessorKey);
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
            if (!ProcessingCodes.IsValid(failureCode))
            {
                throw new ArgumentException("Use a bounded non-sensitive failure code.", nameof(failureCode));
            }
        }
        else if (failureCode is not null)
        {
            throw new ArgumentException("Only failed processing accepts a failure code.", nameof(failureCode));
        }

        string now = CatalogValues.Timestamp(clock.GetUtcNow());
        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = connection.CreateCommand("""
                                                                   UPDATE processing_jobs
                                                                   SET state = $state, lease_key = NULL, lease_until_utc = NULL,
                                                                       failure_code = $failure, updated_utc = $now
                                                                   WHERE item_id = $item AND revision = $revision
                                                                     AND processor_key = $processor AND state = 'running'
                                                                     AND lease_key = $lease AND lease_until_utc > $now
                                                                     AND EXISTS (
                                                                         SELECT 1 FROM items i JOIN sources s ON s.source_id = i.source_id
                                                                         WHERE i.item_id = processing_jobs.item_id AND s.status = 'active'
                                                                           AND processing_jobs.revision = (
                                                                               SELECT MAX(latest.revision) FROM item_versions latest
                                                                               WHERE latest.item_id = processing_jobs.item_id))
                                                                   """);
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$failure", (object?)failureCode ?? DBNull.Value);
            command.Parameters.AddWithValue("$now", now);
            ProcessingJobParameters.BindLease(command, lease);
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
    public ProcessingLease? Renew(ProcessingLease lease, TimeSpan extension)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ProcessingJobKey.Validate(lease.ItemId, lease.Revision, lease.ProcessorKey);
        if (extension < TimeSpan.FromSeconds(1) || extension > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(extension));
        }

        DateTimeOffset time = clock.GetUtcNow();
        string now = CatalogValues.Timestamp(time);
        DateTimeOffset until = time.ToUniversalTime().Add(extension);
        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand superseded = connection.CreateCommand("""
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
            ProcessingJobParameters.BindLease(superseded, lease);
            superseded.Parameters.AddWithValue("$now", now);
            if (superseded.ExecuteNonQuery() == 1)
            {
                transaction.Commit();
                return null;
            }

            using SqliteCommand extend = connection.CreateCommand("""
                                                                  UPDATE processing_jobs
                                                                  SET lease_until_utc = $until, updated_utc = $now
                                                                  WHERE item_id = $item AND revision = $revision AND processor_key = $processor
                                                                    AND state = 'running' AND lease_key = $lease AND lease_until_utc > $now
                                                                    AND EXISTS (
                                                                        SELECT 1 FROM items i JOIN sources s ON s.source_id = i.source_id
                                                                        WHERE i.item_id = processing_jobs.item_id AND s.status = 'active')
                                                                  """);
            extend.Transaction = transaction;
            ProcessingJobParameters.BindLease(extend, lease);
            extend.Parameters.AddWithValue("$until", CatalogValues.Timestamp(until));
            extend.Parameters.AddWithValue("$now", now);
            bool renewed = extend.ExecuteNonQuery() == 1;
            transaction.Commit();
            return renewed ? lease with { LeaseUntil = until } : null;
        });
    }

    /// <summary>
    /// Returns a held lease's job to the queue and refunds the attempt it consumed, for work
    /// that could not start because a runtime dependency was unavailable.
    /// </summary>
    public bool Defer(ProcessingLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ProcessingJobKey.Validate(lease.ItemId, lease.Revision, lease.ProcessorKey);
        string now = CatalogValues.Timestamp(clock.GetUtcNow());
        return database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
                                                                   UPDATE processing_jobs
                                                                   SET state = 'queued', attempt = MAX(attempt - 1, 0),
                                                                       lease_key = NULL, lease_until_utc = NULL, updated_utc = $now
                                                                   WHERE item_id = $item AND revision = $revision AND processor_key = $processor
                                                                     AND state = 'running' AND lease_key = $lease
                                                                     AND EXISTS (
                                                                         SELECT 1 FROM items i JOIN sources s ON s.source_id = i.source_id
                                                                         WHERE i.item_id = processing_jobs.item_id AND s.status = 'active'
                                                                           AND processing_jobs.revision = (
                                                                               SELECT MAX(latest.revision) FROM item_versions latest
                                                                               WHERE latest.item_id = processing_jobs.item_id))
                                                                   """);
            ProcessingJobParameters.BindLease(command, lease);
            command.Parameters.AddWithValue("$now", now);
            return command.ExecuteNonQuery() == 1;
        });
    }

    public bool Retry(Guid itemId, int revision, string processorKey)
    {
        ProcessingJobKey.Validate(itemId, revision, processorKey);
        return database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
                                                                   UPDATE processing_jobs
                                                                   SET state = 'queued', attempt = 0, failure_code = NULL,
                                                                       lease_key = NULL, lease_until_utc = NULL, updated_utc = $now
                                                                   WHERE item_id = $item AND revision = $revision AND processor_key = $processor
                                                                     AND state IN ('failed', 'canceled')
                                                                     AND EXISTS (
                                                                         SELECT 1 FROM items i JOIN sources s ON s.source_id = i.source_id
                                                                         WHERE i.item_id = processing_jobs.item_id AND s.status = 'active'
                                                                           AND processing_jobs.revision = (
                                                                               SELECT MAX(latest.revision) FROM item_versions latest
                                                                               WHERE latest.item_id = processing_jobs.item_id))
                                                                   """);
            command.Parameters.AddWithValue("$now", CatalogValues.Timestamp(clock.GetUtcNow()));
            ProcessingJobParameters.Bind(command, itemId, revision, processorKey);
            return command.ExecuteNonQuery() == 1;
        });
    }

    public bool Cancel(Guid itemId, int revision, string processorKey)
    {
        ProcessingJobKey.Validate(itemId, revision, processorKey);
        return database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
                                                                   UPDATE processing_jobs
                                                                   SET state = 'canceled', failure_code = NULL, lease_key = NULL,
                                                                       lease_until_utc = NULL, updated_utc = $now
                                                                   WHERE item_id = $item AND revision = $revision AND processor_key = $processor
                                                                     AND state IN ('queued', 'running', 'failed')
                                                                   """);
            command.Parameters.AddWithValue("$now", CatalogValues.Timestamp(clock.GetUtcNow()));
            ProcessingJobParameters.Bind(command, itemId, revision, processorKey);
            return command.ExecuteNonQuery() == 1;
        });
    }

    /// <summary>Schedules a newly committed revision inside the caller's capture transaction.</summary>
    internal static void QueueVersion(SqliteConnection connection, SqliteTransaction transaction, Guid itemId, int revision, string processor, string utc)
    {
        using SqliteCommand command = connection.CreateCommand("""
                                                               INSERT INTO processing_jobs
                                                                   (item_id, revision, processor_key, state, attempt, total_attempts,
                                                                    created_utc, updated_utc)
                                                               VALUES ($item, $revision, $processor, 'queued', 0, 0, $now, $now)
                                                               """);
        command.Transaction = transaction;
        ProcessingJobParameters.Bind(command, itemId, revision, processor);
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
                                                                      AND v.revision = processing_jobs.revision AND v.digest = $digest
                                                                      AND v.revision = (
                                                                          SELECT MAX(latest.revision) FROM item_versions latest
                                                                          WHERE latest.item_id = v.item_id))
                                                              """);
        finish.Transaction = transaction;
        ProcessingJobParameters.BindLease(finish, lease);
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
    internal static void QueueDependents(SqliteConnection connection, SqliteTransaction transaction, Guid itemId, int revision, string completedProcessor, string utc)
    {
        foreach (string dependent in ProcessorKeys.DependentsOf(completedProcessor))
        {
            // A format-specific stage (such as link fetching) is scheduled only for that format.
            using SqliteCommand command = connection.CreateCommand("""
                                                                   INSERT INTO processing_jobs
                                                                       (item_id, revision, processor_key, state, attempt, total_attempts,
                                                                        created_utc, updated_utc)
                                                                   SELECT $item, $revision, $processor, 'queued', 0, 0, $now, $now
                                                                   WHERE $format IS NULL OR EXISTS (
                                                                       SELECT 1 FROM intake_documents d
                                                                       WHERE d.item_id = $item AND d.revision = $revision AND d.format = $format)
                                                                   ON CONFLICT(item_id, revision, processor_key) DO NOTHING
                                                                   """);
            command.Transaction = transaction;
            ProcessingJobParameters.Bind(command, itemId, revision, dependent);
            command.Parameters.AddWithValue("$now", utc);
            command.Parameters.AddWithValue("$format", (object?)ProcessorKeys.RequiredFormat(dependent) ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }

    private static void ExpireAttemptLimitedJobs(SqliteConnection connection, SqliteTransaction transaction, string now, int maxAttempts, string? processorKey)
    {
        using SqliteCommand command = connection.CreateCommand("""
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
