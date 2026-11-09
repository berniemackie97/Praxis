using System.Text;

namespace Praxis.Storage;

public sealed record TextDerivative(
    Guid ItemId, int Revision, string SourceSha256, string Outcome,
    string? Content, string? ReasonCode);

internal sealed record LeasedTextSource(string SourceKind, string Locator, OriginalObject Original);

public sealed partial class LocalCatalog
{
    internal LeasedTextSource? ReadLeasedTextSource(ProcessingLease lease)
    {
        ValidateJobIdentity(lease.ItemId, lease.Revision, lease.ProcessorKey);
        lock (connection)
        {
            using var command = Command(connection, """
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
            using var source = Command(connection, """
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
            using var insert = Command(connection, """
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

            using var finish = Command(connection, """
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

    public TextDerivative? ReadTextDerivative(Guid itemId, int revision)
    {
        if (itemId == Guid.Empty || revision < 1)
        {
            throw new ArgumentException("A valid item and revision are required.");
        }

        lock (connection)
        {
            using var command = Command(connection, """
                SELECT d.source_digest, d.outcome, d.content, d.reason_code
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
                ? new TextDerivative(itemId, revision, reader.GetString(0),
                    reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3))
                : null;
        }
    }
}
