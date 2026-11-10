using Microsoft.Data.Sqlite;
using Praxis.Storage.Catalog;
using Praxis.Storage.Processing;

namespace Praxis.Storage.Originals;

public sealed record CapturedOriginal(Guid SourceId, Guid ItemId, int Revision, string Sha256, long Length);

public sealed record LocalOriginalExport(Guid ItemId, int Revision, string Sha256, long Length);

/// <summary>
/// Captures, verifies and exports immutable content-addressed originals. A capture commits the
/// source/item revision, its idempotent request receipt and its initial processing job atomically.
/// </summary>
public sealed class LocalOriginals
{
    private readonly CatalogDatabase database;
    private readonly OriginalFileStore objects;
    private readonly string dataDirectory;

    internal LocalOriginals(CatalogDatabase database, OriginalFileStore objects, string dataDirectory)
    {
        this.database = database;
        this.objects = objects;
        this.dataDirectory = dataDirectory;
    }

    public Task<CapturedOriginal> CaptureFileAsync(string sourcePath, Stream content, long maxBytes, string requestKey, CancellationToken cancellationToken = default) =>
        CaptureFileAsync(sourcePath, content, maxBytes, requestKey, CaptureRequestContext.Unattributed, cancellationToken);

    internal async Task<CapturedOriginal> CaptureFileAsync(string sourcePath, Stream content, long maxBytes, string requestKey, CaptureRequestContext capture, CancellationToken cancellationToken = default, string? expectedDigest = null, long? expectedLength = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
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

        if ((expectedDigest is null) != (expectedLength is null) || expectedLength is < 0)
        {
            throw new ArgumentException("The expected captured source identity is incomplete or invalid.");
        }

        string locator = Path.GetFullPath(sourcePath);
        capture.Validate(locator);
        CatalogDirectory.Validate(dataDirectory);

        CapturedOriginal? receipt = database.Execute(connection => ReadReceipt(connection, null, requestKey, locator, capture));
        if (receipt is not null)
        {
            ValidateExpected(receipt.Sha256, receipt.Length, expectedDigest, expectedLength);
            await objects.VerifyAsync(new OriginalObject(receipt.Sha256, receipt.Length), cancellationToken);
            return receipt;
        }

        OriginalObject original = await objects.PublishAsync(content, maxBytes, cancellationToken);
        ValidateExpected(original.Digest, original.Length, expectedDigest, expectedLength);
        cancellationToken.ThrowIfCancellationRequested();

        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            CapturedOriginal? concurrent = ReadReceipt(connection, transaction, requestKey, locator, capture);
            if (concurrent is not null)
            {
                if (concurrent.Sha256 != original.Digest || concurrent.Length != original.Length)
                {
                    throw new InvalidOperationException("A concurrent capture reused the request key for different content.");
                }

                return concurrent;
            }

            CapturedOriginal recorded = CommitOriginal(connection, transaction, locator, requestKey, original, capture);
            transaction.Commit();
            return recorded;
        });
    }

    private static void ValidateExpected(string digest, long length, string? expectedDigest, long? expectedLength)
    {
        if (expectedDigest is not null &&
            (!string.Equals(digest, expectedDigest, StringComparison.Ordinal) || length != expectedLength))
        {
            throw new InvalidDataException("The source bytes changed between observation and capture.");
        }
    }

    private static CapturedOriginal CommitOriginal(SqliteConnection connection, SqliteTransaction transaction, string locator, string requestKey, OriginalObject original, CaptureRequestContext capture)
    {
        string recordedUtc = CatalogValues.Timestamp(DateTimeOffset.UtcNow);
        using (SqliteCommand check = connection.CreateCommand("SELECT byte_length FROM original_objects WHERE digest=$digest"))
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
        using (SqliteCommand source = connection.CreateCommand("""
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

        using SqliteCommand command = connection.CreateCommand("""
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
            using SqliteCommand source = connection.CreateCommand("""
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
            using SqliteCommand version = connection.CreateCommand("""
                                                                   INSERT INTO item_versions (item_id, revision, digest, observed_utc)
                                                                       VALUES ($item, $revision, $digest, $recorded)
                                                                   """);
            version.Transaction = transaction;
            version.Parameters.AddWithValue("$item", itemId.ToString("D"));
            version.Parameters.AddWithValue("$revision", revision);
            version.Parameters.AddWithValue("$digest", original.Digest);
            version.Parameters.AddWithValue("$recorded", recordedUtc);
            version.ExecuteNonQuery();
            LocalProcessingQueue.QueueVersion(connection, transaction, itemId, revision, LocalProcessors.SourceText, recordedUtc);
        }

        using SqliteCommand receipt = connection.CreateCommand("""
                                                               INSERT INTO ingest_requests (request_key, source_id, state, attempt, failure_code, revision)
                                                                   VALUES ($key, $source, 'committed', 1, NULL, $revision);
                                                               INSERT INTO capture_request_context
                                                                   (request_key, route, authority_basis, scope, recorded_utc)
                                                                   VALUES ($key, $route, $basis, $scope, $recorded);
                                                               """);
        receipt.Transaction = transaction;
        receipt.Parameters.AddWithValue("$key", requestKey);
        receipt.Parameters.AddWithValue("$source", sourceId.ToString("D"));
        receipt.Parameters.AddWithValue("$revision", revision);
        receipt.Parameters.AddWithValue("$route", capture.Route);
        receipt.Parameters.AddWithValue("$basis", capture.AuthorityBasis);
        receipt.Parameters.AddWithValue("$scope", (object?)capture.Scope ?? DBNull.Value);
        receipt.Parameters.AddWithValue("$recorded", recordedUtc);
        receipt.ExecuteNonQuery();

        return new CapturedOriginal(sourceId, itemId, revision, original.Digest, original.Length);
    }

    private static CapturedOriginal? ReadReceipt(SqliteConnection connection, SqliteTransaction? transaction, string requestKey, string locator, CaptureRequestContext capture)
    {
        using SqliteCommand command = connection.CreateCommand("""
                                                               SELECT s.locator, s.status, r.state, s.source_id, i.item_id,
                                                                      r.revision, o.digest, o.byte_length,
                                                                      c.route, c.authority_basis, c.scope
                                                               FROM ingest_requests r
                                                               JOIN sources s ON s.source_id = r.source_id
                                                               JOIN items i ON i.source_id = s.source_id
                                                               LEFT JOIN item_versions v ON v.item_id = i.item_id AND v.revision = r.revision
                                                               LEFT JOIN original_objects o ON o.digest = v.digest
                                                               LEFT JOIN capture_request_context c ON c.request_key = r.request_key
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

        if (!reader.IsDBNull(8) &&
            (reader.GetString(8) != capture.Route ||
             reader.GetString(9) != capture.AuthorityBasis ||
             (!reader.IsDBNull(10) ? reader.GetString(10) : null) != capture.Scope))
        {
            throw new InvalidOperationException("A request key cannot cross capture routes or authority scopes.");
        }

        if (reader.GetString(1) != "active")
        {
            throw new InvalidOperationException("A revoked source is not available for retry.");
        }

        if (reader.GetString(2) != "committed" || reader.IsDBNull(5) || reader.IsDBNull(6) || reader.IsDBNull(7))
        {
            throw new InvalidOperationException("The request has no completed original version.");
        }

        return new CapturedOriginal(Guid.Parse(reader.GetString(3)), Guid.Parse(reader.GetString(4)),
            reader.GetInt32(5), reader.GetString(6), reader.GetInt64(7));
    }

    /// <summary>Reads a whole original after verifying its recorded length and SHA-256.</summary>
    internal Task<byte[]> ReadVerifiedBytesAsync(OriginalObject original, int maxBytes, CancellationToken cancellationToken)
        => objects.ReadVerifiedBytesAsync(original, maxBytes, cancellationToken);

    public async Task VerifyAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        if (itemId == Guid.Empty)
        {
            throw new ArgumentException("The item ID must be nonempty.", nameof(itemId));
        }

        OriginalObject original = database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
                                                                   SELECT o.digest, o.byte_length
                                                                   FROM item_versions v
                                                                   JOIN original_objects o ON o.digest = v.digest
                                                                   WHERE v.item_id = $item
                                                                   ORDER BY v.revision DESC
                                                                   LIMIT 1
                                                                   """);
            command.Parameters.AddWithValue("$item", itemId.ToString("D"));
            using SqliteDataReader reader = command.ExecuteReader();
            return !reader.Read()
                ? throw new KeyNotFoundException("The catalog item has no original version.")
                : new OriginalObject(reader.GetString(0), reader.GetInt64(1));
        });

        await objects.VerifyAsync(original, cancellationToken);
    }

    public async Task<LocalOriginalExport> ExportAsync(Guid itemId, int revision, string destinationPath, CancellationToken cancellationToken = default)
    {
        if (itemId == Guid.Empty || revision < 1)
        {
            throw new ArgumentException("A valid item ID and latest revision are required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!Path.IsPathFullyQualified(destinationPath))
        {
            throw new ArgumentException("The export destination must be absolute.", nameof(destinationPath));
        }

        string requestedDestination = Path.GetFullPath(destinationPath);
        string requestedFolder = Path.GetDirectoryName(requestedDestination)!;
        if (!Directory.Exists(requestedFolder))
        {
            throw new DirectoryNotFoundException(requestedFolder);
        }

        string resolvedCatalog = CatalogPaths.ResolveExisting(dataDirectory);
        string folder = CatalogPaths.ResolveExisting(requestedFolder);
        string destination = Path.Combine(folder, Path.GetFileName(requestedDestination));
        if (CatalogPaths.Contains(resolvedCatalog, destination))
        {
            throw new InvalidOperationException("An export cannot be written inside the private catalog.");
        }

        if (File.Exists(destination) || Directory.Exists(destination))
        {
            throw new IOException("The export destination already exists and cannot be overwritten.");
        }

        OriginalObject original = database.Execute(connection => CurrentExportableOriginal(connection, itemId, revision))
                                  ?? throw new InvalidOperationException("The selected source version is not active or current.");

        string pending = Path.Combine(folder, ".praxis-export-" + Guid.NewGuid().ToString("N") + ".part");
        try
        {
            FileStreamOptions options = new()
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            await using (FileStream output = new(pending, options))
            {
                await objects.CopyVerifiedAsync(original, output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            // Publish under the catalog gate so revocation cannot interleave with the final check.
            database.Execute(connection =>
            {
                if (CurrentExportableOriginal(connection, itemId, revision) != original)
                {
                    throw new InvalidOperationException("The source changed or was revoked during export.");
                }

                File.Move(pending, destination);
            });

            return new LocalOriginalExport(itemId, revision, original.Digest, original.Length);
        }
        finally
        {
            File.Delete(pending);
        }
    }

    private static OriginalObject? CurrentExportableOriginal(SqliteConnection connection, Guid itemId, int revision)
    {
        using SqliteCommand query = connection.CreateCommand("""
                                                             SELECT v.digest, o.byte_length
                                                             FROM item_versions v
                                                             JOIN items i ON i.item_id = v.item_id
                                                             JOIN sources s ON s.source_id = i.source_id
                                                             JOIN original_objects o ON o.digest = v.digest
                                                             WHERE v.item_id = $item AND v.revision = $revision
                                                               AND s.status = 'active'
                                                               AND v.revision = (
                                                                   SELECT MAX(latest.revision) FROM item_versions latest
                                                                   WHERE latest.item_id = v.item_id)
                                                             """);
        query.Parameters.AddWithValue("$item", itemId.ToString("D"));
        query.Parameters.AddWithValue("$revision", revision);
        using SqliteDataReader reader = query.ExecuteReader();
        return reader.Read() ? new OriginalObject(reader.GetString(0), reader.GetInt64(1)) : null;
    }
}
