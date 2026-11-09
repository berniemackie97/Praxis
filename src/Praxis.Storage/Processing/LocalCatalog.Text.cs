using System.Globalization;
using System.Text;

namespace Praxis.Storage;

public sealed record TextDerivative(
    Guid SourceId, Guid ItemId, int Revision, string ProcessorKey, string SourceSha256,
    string Outcome, string? Content, string? ReasonCode, DateTimeOffset ProducedUtc);

internal sealed record LeasedTextSource(string SourceKind, string Locator, OriginalObject Original);

public sealed partial class LocalCatalog
{
    internal LeasedTextSource? ReadLeasedTextSource(ProcessingLease lease)
    {
        ValidateJobIdentity(lease.ItemId, lease.Revision, lease.ProcessorKey);
        lock (connection)
        {
            using var command = connection.CreateCommand("""
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
            BindJob(command, lease.ItemId, lease.Revision, lease.ProcessorKey);
            command.Parameters.AddWithValue("$lease", lease.LeaseKey.ToString("D"));
            command.Parameters.AddWithValue("$now", Timestamp(DateTimeOffset.UtcNow));
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new LeasedTextSource(reader.GetString(0), reader.GetString(1),
                    new OriginalObject(reader.GetString(2), reader.GetInt64(3)))
                : null;
        }
    }

    internal bool CommitTextResult(
        ProcessingLease lease, string? content, string? reasonCode)
    {
        ValidateJobIdentity(lease.ItemId, lease.Revision, lease.ProcessorKey);
        if ((content is null) == (reasonCode is null))
        {
            throw new ArgumentException("Provide either extracted text or an unsupported reason.");
        }

        if (content is not null &&
            Encoding.UTF8.GetByteCount(content) > LocalTextProcessor.MaximumTextBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(content));
        }

        if (reasonCode is not null && (reasonCode.Length is < 1 or > 64 ||
            reasonCode.Any(c => c is not (>= 'a' and <= 'z' or >= '0' and <= '9' or '_'))))
        {
            throw new ArgumentException("Use a bounded unsupported reason code.", nameof(reasonCode));
        }

        var now = Timestamp(DateTimeOffset.UtcNow);
        lock (connection)
        {
            using var transaction = connection.BeginTransaction();
            using var source = connection.CreateCommand("""
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
            BindJob(source, lease.ItemId, lease.Revision, lease.ProcessorKey);
            source.Parameters.AddWithValue("$lease", lease.LeaseKey.ToString("D"));
            source.Parameters.AddWithValue("$now", now);
            if (source.ExecuteScalar() is not string sourceDigest)
            {
                return false;
            }

            var outcome = content is null ? "unsupported" : "completed";
            using var insert = connection.CreateCommand("""
                INSERT INTO text_derivatives
                    (item_id, revision, processor_key, source_digest,
                     outcome, content, reason_code, created_utc)
                VALUES ($item, $revision, $processor, $digest,
                        $outcome, $content, $reason, $now)
                """);
            insert.Transaction = transaction;
            BindJob(insert, lease.ItemId, lease.Revision, lease.ProcessorKey);
            insert.Parameters.AddWithValue("$digest", sourceDigest);
            insert.Parameters.AddWithValue("$outcome", outcome);
            insert.Parameters.AddWithValue("$content", (object?)content ?? DBNull.Value);
            insert.Parameters.AddWithValue("$reason", (object?)reasonCode ?? DBNull.Value);
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();

            using var finish = connection.CreateCommand("""
                UPDATE processing_jobs
                SET state = $outcome, lease_key = NULL, lease_until_utc = NULL,
                    failure_code = NULL, updated_utc = $now
                WHERE item_id = $item AND revision = $revision
                  AND processor_key = $processor AND state = 'running'
                  AND lease_key = $lease AND lease_until_utc > $now
                """);
            finish.Transaction = transaction;
            BindJob(finish, lease.ItemId, lease.Revision, lease.ProcessorKey);
            finish.Parameters.AddWithValue("$outcome", outcome);
            finish.Parameters.AddWithValue("$now", now);
            finish.Parameters.AddWithValue("$lease", lease.LeaseKey.ToString("D"));
            if (finish.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("The processing result lost its lease.");
            }

            transaction.Commit();
            return true;
        }
    }


    // Revise only active, current sources previously classified as unsupported solely
    // because their file extension had no text decoder. Other outcomes remain immutable.
    internal bool RequeueNewlySupportedText(IReadOnlyList<string> extensions)
    {
        if (extensions.Count == 0)
        {
            return false;
        }

        var suffixMatch = string.Join(" OR ", Enumerable.Range(0, extensions.Count)
            .Select(index => $"substr(lower(s.locator), -length($extension{index})) = $extension{index}"));
        lock (connection)
        {
            using var transaction = connection.BeginTransaction();
            using var find = connection.CreateCommand($"""
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
            for (var index = 0; index < extensions.Count; index++)
            {
                find.Parameters.AddWithValue($"$extension{index}", extensions[index]);
            }

            string item;
            int revision;
            using (var reader = find.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return false;
                }

                item = reader.GetString(0);
                revision = reader.GetInt32(1);
            }

            using var remove = connection.CreateCommand("""
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

            using var requeue = connection.CreateCommand("""
                UPDATE processing_jobs
                SET state = 'queued', attempt = 0, lease_key = NULL,
                    lease_until_utc = NULL, failure_code = NULL, updated_utc = $now
                WHERE item_id = $item AND revision = $revision
                  AND processor_key = 'source-text-v1' AND state = 'unsupported'
                """);
            requeue.Transaction = transaction;
            requeue.Parameters.AddWithValue("$item", item);
            requeue.Parameters.AddWithValue("$revision", revision);
            requeue.Parameters.AddWithValue("$now", Timestamp(DateTimeOffset.UtcNow));
            if (requeue.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("The unsupported processing job changed.");
            }

            transaction.Commit();
            return true;
        }
    }

    public TextDerivative? ReadTextDerivative(Guid itemId, int revision)
    {
        if (itemId == Guid.Empty || revision < 1)
        {
            throw new ArgumentException("A valid item and revision are required.");
        }

        lock (connection)
        {
            using var command = connection.CreateCommand("""
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
            command.Parameters.AddWithValue("$item", itemId.ToString("D"));
            command.Parameters.AddWithValue("$revision", revision);
            command.Parameters.AddWithValue("$processor", DefaultProcessorKey);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new TextDerivative(Guid.Parse(reader.GetString(0)), itemId, revision,
                    DefaultProcessorKey, reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind))
                : null;
        }
    }
}
