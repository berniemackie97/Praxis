using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Praxis.Storage;

public sealed record CapturedOriginal(Guid SourceId, Guid ItemId, int Revision, string Sha256, long Length);

public sealed partial class LocalCatalog
{
    public async Task<CapturedOriginal> CaptureFileAsync(
        string sourcePath, Stream content, long maxBytes, string requestKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestKey);
        ArgumentNullException.ThrowIfNull(content);
        if (!Path.IsPathFullyQualified(sourcePath))
        {
            throw new ArgumentException("The source locator must be an absolute path.", nameof(sourcePath));
        }

        if (requestKey.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(requestKey), "Request keys cannot exceed 128 characters.");
        }

        string locator = Path.GetFullPath(sourcePath);
        string root = Path.GetDirectoryName(DatabasePath)!;
        ValidateRoot(root);

        CapturedOriginal? receipt;
        lock (connection)
        {
            receipt = ReadReceipt(requestKey, locator);
        }

        if (receipt is not null)
        {
            await new OriginalFileStore(root).VerifyAsync(
                new OriginalObject(receipt.Sha256, receipt.Length), cancellationToken);
            return receipt;
        }

        OriginalObject original = await new OriginalFileStore(root)
            .PublishAsync(content, maxBytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        CapturedOriginal recorded;
        lock (connection)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            receipt = ReadReceipt(requestKey, locator, transaction);
            if (receipt is not null)
            {
                if (receipt.Sha256 != original.Digest || receipt.Length != original.Length)
                {
                    throw new InvalidOperationException("A concurrent capture reused the request key for different content.");
                }

                recorded = receipt;
            }
            else
            {
                recorded = CommitOriginal(locator, requestKey, original, transaction);
                transaction.Commit();
            }
        }

        return recorded;
    }

    private CapturedOriginal CommitOriginal(
        string locator, string requestKey, OriginalObject original, SqliteTransaction transaction)
    {
        string recordedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        using (SqliteCommand check = Command(connection,
            "SELECT byte_length FROM original_objects WHERE digest=$digest"))
        {
            check.Transaction = transaction;
            check.Parameters.AddWithValue("$digest", original.Digest);
            if (check.ExecuteScalar() is long existingLength && existingLength != original.Length)
            {
                throw new InvalidDataException("The object digest has a conflicting recorded length.");
            }
        }

        Guid sourceId, itemId;
        int revision;
        string? previousDigest = null;
        using (SqliteCommand source = Command(connection, """
                                                          SELECT s.source_id, s.status, i.item_id, v.revision, v.digest
                                                          FROM sources s
                                                          JOIN items i ON i.source_id = s.source_id
                                                          JOIN item_versions v ON v.item_id = i.item_id
                                                          WHERE s.source_kind = 'file' AND s.locator = $locator
                                                          ORDER BY v.revision DESC LIMIT 1
                                                          """))
        {
            source.Transaction = transaction;
            source.Parameters.AddWithValue("$locator", locator);
            using SqliteDataReader reader = source.ExecuteReader();
            if (reader.Read())
            {
                if (reader.GetString(1) != "active")
                {
                    throw new InvalidOperationException("A revoked source cannot be recaptured implicitly.");
                }

                sourceId = Guid.Parse(reader.GetString(0));
                itemId = Guid.Parse(reader.GetString(2));
                revision = reader.GetInt32(3);
                previousDigest = reader.GetString(4);
                if (previousDigest != original.Digest)
                {
                    revision = checked(revision + 1);
                }
            }
            else
            {
                sourceId = Guid.NewGuid();
                itemId = Guid.NewGuid();
                revision = 1;
            }
        }

        using SqliteCommand command = Command(connection, """
                                                          INSERT INTO original_objects (digest, byte_length, recorded_utc)
                                                              VALUES ($digest, $length, $recorded)
                                                              ON CONFLICT(digest) DO NOTHING;
                                                          """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$digest", original.Digest);
        command.Parameters.AddWithValue("$length", original.Length);
        command.Parameters.AddWithValue("$recorded", recordedUtc);
        command.ExecuteNonQuery();

        if (previousDigest is null)
        {
            using SqliteCommand source = Command(connection, """
                                                             INSERT INTO sources (source_id, source_kind, locator, created_utc)
                                                                 VALUES ($source, 'file', $locator, $recorded);
                                                             INSERT INTO items (item_id, source_id) VALUES ($item, $source);
                                                             """);
            source.Transaction = transaction;
            source.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            source.Parameters.AddWithValue("$item", itemId.ToString("D"));
            source.Parameters.AddWithValue("$locator", locator);
            source.Parameters.AddWithValue("$recorded", recordedUtc);
            source.ExecuteNonQuery();
        }

        if (previousDigest != original.Digest)
        {
            using SqliteCommand version = Command(connection, """
                                                              INSERT INTO item_versions (item_id, revision, digest, observed_utc)
                                                                  VALUES ($item, $revision, $digest, $recorded)
                                                              """);
            version.Transaction = transaction;
            version.Parameters.AddWithValue("$item", itemId.ToString("D"));
            version.Parameters.AddWithValue("$revision", revision);
            version.Parameters.AddWithValue("$digest", original.Digest);
            version.Parameters.AddWithValue("$recorded", recordedUtc);
            version.ExecuteNonQuery();
            QueueProcessingVersion(itemId, revision, DefaultProcessorKey, recordedUtc, transaction);
        }

        using SqliteCommand receipt = Command(connection, """
                                                          INSERT INTO ingest_requests (request_key, source_id, state, attempt, failure_code, revision)
                                                              VALUES ($key, $source, 'committed', 1, NULL, $revision)
                                                          """);
        receipt.Transaction = transaction;
        receipt.Parameters.AddWithValue("$key", requestKey);
        receipt.Parameters.AddWithValue("$source", sourceId.ToString("D"));
        receipt.Parameters.AddWithValue("$revision", revision);
        receipt.ExecuteNonQuery();

        return new CapturedOriginal(sourceId, itemId, revision, original.Digest, original.Length);
    }

    private CapturedOriginal? ReadReceipt(
        string requestKey, string locator, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = Command(connection, """
                                                          SELECT s.locator, s.status, r.state, s.source_id, i.item_id,
                                                                 r.revision, o.digest, o.byte_length
                                                          FROM ingest_requests r
                                                          JOIN sources s ON s.source_id = r.source_id
                                                          JOIN items i ON i.source_id = s.source_id
                                                          LEFT JOIN item_versions v ON v.item_id = i.item_id AND v.revision = r.revision
                                                          LEFT JOIN original_objects o ON o.digest = v.digest
                                                          WHERE r.request_key = $key
                                                          """);
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$key", requestKey);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        if (!string.Equals(locator, reader.GetString(0), StringComparison.Ordinal))
        {
            throw new InvalidOperationException("A request key cannot be reused for another source.");
        }

        if (reader.GetString(1) != "active")
        {
            throw new InvalidOperationException("A revoked source is not available for retry.");
        }

        if (reader.GetString(2) != "committed" || reader.IsDBNull(5) ||
            reader.IsDBNull(6) || reader.IsDBNull(7))
        {
            throw new InvalidOperationException("The request has no completed original version.");
        }

        return new CapturedOriginal(
            Guid.Parse(reader.GetString(3)), Guid.Parse(reader.GetString(4)),
            reader.GetInt32(5), reader.GetString(6), reader.GetInt64(7));
    }

    public async Task VerifyOriginalAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("The item ID must be nonempty.", nameof(itemId));
        }

        OriginalObject original;
        lock (connection)
        {
            using SqliteCommand command = Command(connection, """
                                                              SELECT o.digest, o.byte_length
                                                              FROM item_versions v
                                                              JOIN original_objects o ON o.digest = v.digest
                                                              WHERE v.item_id = $item
                                                              ORDER BY v.revision DESC
                                                              LIMIT 1
                                                              """);
            command.Parameters.AddWithValue("$item", itemId.ToString("D"));
            using SqliteDataReader reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new KeyNotFoundException("The catalog item has no original version.");
            }

            original = new OriginalObject(reader.GetString(0), reader.GetInt64(1));
        }

        await new OriginalFileStore(Path.GetDirectoryName(DatabasePath)!)
            .VerifyAsync(original, cancellationToken);
    }
}
