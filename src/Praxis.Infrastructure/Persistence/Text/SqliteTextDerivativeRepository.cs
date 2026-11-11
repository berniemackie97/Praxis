using System.Text;
using Microsoft.Data.Sqlite;
using Praxis.Application.Text;
using Praxis.Domain.Originals;
using Praxis.Domain.Processing;
using Praxis.Domain.Text;
using Praxis.Infrastructure.Persistence.Processing;

namespace Praxis.Infrastructure.Persistence.Text;

/// <summary>
/// Revision- and digest-bound text extracted from immutable originals. A result is committed
/// atomically with its job's terminal state and is readable only while its source is active.
/// </summary>
public sealed class SqliteTextDerivativeRepository : ITextDerivativeRepository
{
    private readonly CatalogDatabase database;

    private readonly TimeProvider clock;

    internal SqliteTextDerivativeRepository(CatalogDatabase database, TimeProvider clock)
    {
        this.database = database;
        this.clock = clock;
    }

    public TextDerivative? Read(Guid itemId, int revision)
    {
        if (itemId == Guid.Empty || revision < 1)
        {
            throw new ArgumentException("A valid item and revision are required.");
        }

        return database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
                                                                   SELECT s.source_id, d.source_digest, d.outcome, d.content, d.reason_code, d.created_utc
                                                                   FROM text_derivatives d
                                                                   JOIN processing_jobs j ON j.item_id = d.item_id
                                                                       AND j.revision = d.revision AND j.processor_key = d.processor_key
                                                                   JOIN item_versions v ON v.item_id = d.item_id AND v.revision = d.revision
                                                                       AND v.digest = d.source_digest
                                                                   JOIN items i ON i.item_id = d.item_id
                                                                   JOIN sources s ON s.source_id = i.source_id
                                                                   WHERE d.item_id = $item AND d.revision = $revision
                                                                     AND d.processor_key = $processor AND s.status = 'active'
                                                                     AND j.state = d.outcome
                                                                   """);
            ProcessingJobParameters.Bind(command, itemId, revision, ProcessorKeys.SourceText);
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read()
                ? new TextDerivative(Guid.Parse(reader.GetString(0)), itemId, revision,
                    ProcessorKeys.SourceText, reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    CatalogValues.ParseTimestamp(reader.GetString(5)))
                : null;
        });
    }

    public LeasedTextSource? ReadLeasedSource(ProcessingLease lease)
    {
        ProcessingJobKey.Validate(lease.ItemId, lease.Revision, lease.ProcessorKey);
        return database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
                                                                   SELECT s.source_kind, s.locator, v.digest, o.byte_length
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
                ? new LeasedTextSource(reader.GetString(0), reader.GetString(1),
                    new OriginalObject(reader.GetString(2), reader.GetInt64(3)))
                : null;
        });
    }

    /// <summary>
    /// Completed, digest-matched text of the leased job's revision for a downstream processor,
    /// or null when the lease is no longer held or the source was revoked.
    /// </summary>
    public LeasedText? ReadLeasedInput(ProcessingLease lease)
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
            ProcessingJobParameters.BindLease(query, lease);
            query.Parameters.AddWithValue("$now", CatalogValues.Timestamp(clock.GetUtcNow()));
            using SqliteDataReader reader = query.ExecuteReader();
            return reader.Read()
                ? new LeasedText(reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3))
                : null;
        });
    }

    public bool CommitResult(ProcessingLease lease, string? content, string? reasonCode)
    {
        ProcessingJobKey.Validate(lease.ItemId, lease.Revision, lease.ProcessorKey);
        if ((content is null) == (reasonCode is null))
        {
            throw new ArgumentException("Provide either extracted text or an unsupported reason.");
        }

        if (content is not null &&
            Encoding.UTF8.GetByteCount(content) > TextLimits.MaximumTextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(content));
        }

        if (reasonCode is not null && !ProcessingCodes.IsValid(reasonCode))
        {
            throw new ArgumentException("Use a bounded unsupported reason code.", nameof(reasonCode));
        }

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
            if (source.ExecuteScalar() is not string sourceDigest)
            {
                return false;
            }

            string outcome = content is null ? "unsupported" : "completed";
            using SqliteCommand insert = connection.CreateCommand("""
                                                                  INSERT INTO text_derivatives
                                                                      (item_id, revision, processor_key, source_digest,
                                                                       outcome, content, reason_code, created_utc)
                                                                  VALUES ($item, $revision, $processor, $digest,
                                                                          $outcome, $content, $reason, $now)
                                                                  """);
            insert.Transaction = transaction;
            ProcessingJobParameters.Bind(insert, lease.ItemId, lease.Revision, lease.ProcessorKey);
            insert.Parameters.AddWithValue("$digest", sourceDigest);
            insert.Parameters.AddWithValue("$outcome", outcome);
            insert.Parameters.AddWithValue("$content", (object?)content ?? DBNull.Value);
            insert.Parameters.AddWithValue("$reason", (object?)reasonCode ?? DBNull.Value);
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();

            using SqliteCommand finish = connection.CreateCommand("""
                                                                  UPDATE processing_jobs
                                                                  SET state = $outcome, lease_key = NULL, lease_until_utc = NULL,
                                                                      failure_code = NULL, updated_utc = $now
                                                                  WHERE item_id = $item AND revision = $revision
                                                                    AND processor_key = $processor AND state = 'running'
                                                                    AND lease_key = $lease AND lease_until_utc > $now
                                                                  """);
            finish.Transaction = transaction;
            ProcessingJobParameters.BindLease(finish, lease);
            finish.Parameters.AddWithValue("$outcome", outcome);
            finish.Parameters.AddWithValue("$now", now);
            if (finish.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("The processing result lost its lease.");
            }

            if (content is not null)
            {
                SqliteProcessingQueue.QueueDependents(connection, transaction,
                    lease.ItemId, lease.Revision, lease.ProcessorKey, now);
            }

            transaction.Commit();
            return true;
        });
    }

    /// <summary>
    /// Reopens one active, current source previously classified unsupported only because its
    /// extension had no decoder. Every other recorded outcome remains immutable.
    /// </summary>
    public bool RequeueNewlySupported(IReadOnlyList<string> extensions)
    {
        if (extensions.Count == 0)
        {
            return false;
        }

        string suffixMatch = string.Join(" OR ", Enumerable.Range(0, extensions.Count)
            .Select(index => $"substr(lower(s.locator), -length($extension{index})) = $extension{index}"));
        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand find = connection.CreateCommand($"""
                                                                 SELECT d.item_id, d.revision
                                                                 FROM text_derivatives d
                                                                 JOIN processing_jobs j ON j.item_id = d.item_id
                                                                     AND j.revision = d.revision AND j.processor_key = d.processor_key
                                                                 JOIN item_versions v ON v.item_id = d.item_id
                                                                     AND v.revision = d.revision AND v.digest = d.source_digest
                                                                 JOIN items i ON i.item_id = d.item_id
                                                                 JOIN sources s ON s.source_id = i.source_id
                                                                 WHERE d.processor_key = 'source-text-v1'
                                                                   AND d.outcome = 'unsupported' AND d.reason_code = 'unsupported_format'
                                                                   AND j.state = 'unsupported' AND s.status = 'active'
                                                                   AND s.source_kind = 'file'
                                                                   AND d.revision = (SELECT MAX(latest.revision) FROM item_versions latest
                                                                                     WHERE latest.item_id = d.item_id)
                                                                   AND ({suffixMatch})
                                                                 ORDER BY d.created_utc, d.item_id LIMIT 1
                                                                 """);
            find.Transaction = transaction;
            for (int index = 0; index < extensions.Count; index++)
            {
                find.Parameters.AddWithValue($"$extension{index}", extensions[index]);
            }

            string item;
            int revision;
            using (SqliteDataReader reader = find.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return false;
                }

                item = reader.GetString(0);
                revision = reader.GetInt32(1);
            }

            using SqliteCommand remove = connection.CreateCommand("""
                                                                  DELETE FROM text_derivatives
                                                                  WHERE item_id = $item AND revision = $revision
                                                                    AND processor_key = 'source-text-v1'
                                                                    AND outcome = 'unsupported' AND reason_code = 'unsupported_format'
                                                                  """);
            remove.Transaction = transaction;
            remove.Parameters.AddWithValue("$item", item);
            remove.Parameters.AddWithValue("$revision", revision);
            if (remove.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("The unsupported derivative changed.");
            }

            using SqliteCommand requeue = connection.CreateCommand("""
                                                                   UPDATE processing_jobs
                                                                   SET state = 'queued', attempt = 0, lease_key = NULL,
                                                                       lease_until_utc = NULL, failure_code = NULL, updated_utc = $now
                                                                   WHERE item_id = $item AND revision = $revision
                                                                     AND processor_key = 'source-text-v1' AND state = 'unsupported'
                                                                   """);
            requeue.Transaction = transaction;
            requeue.Parameters.AddWithValue("$item", item);
            requeue.Parameters.AddWithValue("$revision", revision);
            requeue.Parameters.AddWithValue("$now", CatalogValues.Timestamp(clock.GetUtcNow()));
            if (requeue.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("The unsupported processing job changed.");
            }

            transaction.Commit();
            return true;
        });
    }
}
