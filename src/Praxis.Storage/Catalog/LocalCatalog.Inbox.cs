namespace Praxis.Storage;

public sealed record LocalFileVersion(
    Guid SourceId, Guid ItemId, int Revision, string Sha256, string Status);

public sealed record LocalLibraryItem(
    Guid SourceId, Guid ItemId, string SourceKind, string SourceLocator,
    string SourceStatus, int Revision, string Sha256, long OriginalBytes,
    string? ProcessingState, string? ExtractionOutcome);

public sealed partial class LocalCatalog
{
    public LocalFileVersion? FindLatestFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The source path must be absolute.", nameof(path));
        }

        lock (connection)
        {
            using var command = connection.CreateCommand("""
                SELECT s.source_id, i.item_id, v.revision, v.digest, s.status
                FROM sources s
                JOIN items i ON i.source_id = s.source_id
                JOIN item_versions v ON v.item_id = i.item_id
                WHERE s.source_kind = 'file' AND s.locator = $path
                ORDER BY v.revision DESC LIMIT 1
                """);
            command.Parameters.AddWithValue("$path", Path.GetFullPath(path));
            using var reader = command.ExecuteReader();
            return !reader.Read() ? null : new LocalFileVersion(
                Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                reader.GetInt32(2), reader.GetString(3), reader.GetString(4));
        }
    }

    public IReadOnlyList<LocalLibraryItem> ListLibrary(
        int limit = 100, int offset = 0, Guid? collectionId = null)
    {
        if (limit is < 1 or > 200 || offset < 0)
        {
            throw new ArgumentOutOfRangeException(
                limit is < 1 or > 200 ? nameof(limit) : nameof(offset));
        }

        if (collectionId == Guid.Empty)
        {
            throw new ArgumentException("A nonempty collection identifier is required.", nameof(collectionId));
        }

        lock (connection)
        {
            using var command = connection.CreateCommand("""
                SELECT s.source_id, i.item_id, s.source_kind, s.locator, s.status,
                       v.revision, v.digest, o.byte_length, j.state, d.outcome
                FROM items i
                JOIN sources s ON s.source_id = i.source_id
                JOIN item_versions v ON v.item_id = i.item_id
                JOIN original_objects o ON o.digest = v.digest
                LEFT JOIN processing_jobs j
                    ON j.item_id = v.item_id AND j.revision = v.revision
                    AND j.processor_key = 'source-text-v1'
                LEFT JOIN text_derivatives d
                    ON d.item_id = v.item_id AND d.revision = v.revision
                    AND d.processor_key = 'source-text-v1'
                    AND d.source_digest = v.digest
                WHERE v.revision = (
                    SELECT MAX(latest.revision) FROM item_versions latest
                    WHERE latest.item_id = i.item_id
                )
                  AND ($collection IS NULL OR (s.status = 'active' AND EXISTS (
                      SELECT 1 FROM source_collection_memberships membership
                      JOIN source_collections c
                        ON c.collection_id = membership.collection_id
                      WHERE membership.source_id = s.source_id
                        AND membership.collection_id = $collection
                        AND c.archived_utc IS NULL
                  )))
                ORDER BY s.created_utc DESC, i.item_id
                LIMIT $limit OFFSET $offset
                """);
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);
            command.Parameters.AddWithValue("$collection",
                collectionId.HasValue ? (object)collectionId.Value.ToString("D") : DBNull.Value);
            using var reader = command.ExecuteReader();
            var result = new List<LocalLibraryItem>();
            while (reader.Read())
            {
                result.Add(new LocalLibraryItem(
                    Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetInt32(5), reader.GetString(6), reader.GetInt64(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9)));
            }

            return result;
        }
    }
}
