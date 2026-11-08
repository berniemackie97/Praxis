using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Praxis.Storage;

public sealed record CapturedOriginal(Guid SourceId, Guid ItemId, string Sha256, long Length);

public sealed partial class LocalCatalog
{
    public async Task<CapturedOriginal> CaptureFileAsync(
        string sourcePath, Stream content, long maxBytes, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!Path.IsPathFullyQualified(sourcePath))
        {
            throw new ArgumentException("The source locator must be an absolute path.", nameof(sourcePath));
        }

        var locator = Path.GetFullPath(sourcePath);
        var root = Path.GetDirectoryName(DatabasePath)!;
        ValidateRoot(root);
        var original = await new OriginalFileStore(root)
            .PublishAsync(content, maxBytes, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var sourceId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var recordedUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        lock (connection)
        {
            using var transaction = connection.BeginTransaction();
            using (var check = Command(connection,
                "SELECT byte_length FROM original_objects WHERE digest=$digest"))
            {
                check.Transaction = transaction;
                check.Parameters.AddWithValue("$digest", original.Digest);
                if (check.ExecuteScalar() is long existingLength && existingLength != original.Length)
                {
                    throw new InvalidDataException("The object digest has a conflicting recorded length.");
                }
            }

            using var command = Command(connection, """
                INSERT INTO original_objects (digest, byte_length, recorded_utc)
                    VALUES ($digest, $length, $recorded)
                    ON CONFLICT(digest) DO NOTHING;
                INSERT INTO sources (source_id, source_kind, locator, created_utc)
                    VALUES ($source, 'file', $locator, $recorded);
                INSERT INTO items (item_id, source_id)
                    VALUES ($item, $source);
                INSERT INTO item_versions (item_id, revision, digest, observed_utc)
                    VALUES ($item, 1, $digest, $recorded);
                """);
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$digest", original.Digest);
            command.Parameters.AddWithValue("$length", original.Length);
            command.Parameters.AddWithValue("$recorded", recordedUtc);
            command.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            command.Parameters.AddWithValue("$item", itemId.ToString("D"));
            command.Parameters.AddWithValue("$locator", locator);
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        return new CapturedOriginal(sourceId, itemId, original.Digest, original.Length);
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
            using var command = Command(connection, """
                SELECT o.digest, o.byte_length
                FROM item_versions v
                JOIN original_objects o ON o.digest = v.digest
                WHERE v.item_id = $item
                ORDER BY v.revision DESC
                LIMIT 1
                """);
            command.Parameters.AddWithValue("$item", itemId.ToString("D"));
            using var reader = command.ExecuteReader();
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
