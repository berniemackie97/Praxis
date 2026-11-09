using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Praxis.Storage;

public sealed record LocalSourceCollection(
    Guid CollectionId, string Name, int ActiveSourceCount);

public sealed partial class LocalCatalog
{
    public LocalSourceCollection CreateCollection(string name)
    {
        var (display, key) = NormalizeCollectionName(name);
        var id = Guid.NewGuid();
        lock (connection)
        {
            using var command = Command(connection, """
                INSERT INTO source_collections (collection_id, name, name_key, created_utc)
                VALUES ($id, $name, $key, $now)
                """);
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$name", display);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$now", Timestamp(DateTimeOffset.UtcNow));
            try
            {
                command.ExecuteNonQuery();
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException("A collection with this name already exists.", exception);
            }
        }

        return new LocalSourceCollection(id, display, 0);
    }

    public bool RenameCollection(Guid collectionId, string name)
    {
        ValidateCollectionId(collectionId);
        var (display, key) = NormalizeCollectionName(name);
        lock (connection)
        {
            using var command = Command(connection, """
                UPDATE source_collections SET name = $name, name_key = $key
                WHERE collection_id = $id AND archived_utc IS NULL
                """);
            command.Parameters.AddWithValue("$id", collectionId.ToString("D"));
            command.Parameters.AddWithValue("$name", display);
            command.Parameters.AddWithValue("$key", key);
            try
            {
                return command.ExecuteNonQuery() != 0;
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException("A collection with this name already exists.", exception);
            }
        }
    }

    public bool ArchiveCollection(Guid collectionId)
    {
        ValidateCollectionId(collectionId);
        lock (connection)
        {
            using var command = Command(connection, """
                UPDATE source_collections SET archived_utc = $now
                WHERE collection_id = $id AND archived_utc IS NULL
                """);
            command.Parameters.AddWithValue("$id", collectionId.ToString("D"));
            command.Parameters.AddWithValue("$now", Timestamp(DateTimeOffset.UtcNow));
            return command.ExecuteNonQuery() != 0;
        }
    }

    public IReadOnlyList<LocalSourceCollection> ListCollections(int limit = 100, int offset = 0)
    {
        ValidateCollectionPage(limit, offset);
        lock (connection)
        {
            using var command = Command(connection, """
                SELECT c.collection_id, c.name, (
                    SELECT COUNT(*) FROM source_collection_memberships m
                    JOIN sources s ON s.source_id = m.source_id
                    WHERE m.collection_id = c.collection_id AND s.status = 'active'
                )
                FROM source_collections c
                WHERE c.archived_utc IS NULL
                ORDER BY c.name_key, c.collection_id
                LIMIT $limit OFFSET $offset
                """);
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);
            using var reader = command.ExecuteReader();
            var rows = new List<LocalSourceCollection>();
            while (reader.Read())
            {
                rows.Add(new LocalSourceCollection(
                    Guid.Parse(reader.GetString(0)), reader.GetString(1),
                    checked((int)reader.GetInt64(2))));
            }

            return rows;
        }
    }

    public IReadOnlyList<Guid> ListSourceCollectionIds(Guid sourceId, int limit = 100)
    {
        ValidateCollectionId(sourceId);
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        lock (connection)
        {
            using var command = Command(connection, """
                SELECT c.collection_id
                FROM source_collection_memberships m
                JOIN source_collections c ON c.collection_id = m.collection_id
                JOIN sources s ON s.source_id = m.source_id
                WHERE m.source_id = $source AND s.status = 'active'
                  AND c.archived_utc IS NULL
                ORDER BY c.name_key, c.collection_id
                LIMIT $limit
                """);
            command.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            command.Parameters.AddWithValue("$limit", limit);
            using var reader = command.ExecuteReader();
            var rows = new List<Guid>();
            while (reader.Read())
            {
                rows.Add(Guid.Parse(reader.GetString(0)));
            }

            return rows;
        }
    }

    public bool AddSourceToCollection(Guid collectionId, Guid sourceId)
    {
        ValidateCollectionId(collectionId);
        ValidateCollectionId(sourceId);
        lock (connection)
        {
            using var command = Command(connection, """
                INSERT INTO source_collection_memberships
                    (collection_id, source_id, created_utc)
                SELECT c.collection_id, s.source_id, $now
                FROM source_collections c
                JOIN sources s ON s.source_id = $source
                WHERE c.collection_id = $collection
                  AND c.archived_utc IS NULL AND s.status = 'active'
                ON CONFLICT (collection_id, source_id) DO NOTHING
                """);
            command.Parameters.AddWithValue("$collection", collectionId.ToString("D"));
            command.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            command.Parameters.AddWithValue("$now", Timestamp(DateTimeOffset.UtcNow));
            return command.ExecuteNonQuery() != 0;
        }
    }

    public bool RemoveSourceFromCollection(Guid collectionId, Guid sourceId)
    {
        ValidateCollectionId(collectionId);
        ValidateCollectionId(sourceId);
        lock (connection)
        {
            using var command = Command(connection, """
                DELETE FROM source_collection_memberships
                WHERE collection_id = $collection AND source_id = $source
                  AND EXISTS (
                      SELECT 1 FROM source_collections c
                      WHERE c.collection_id = $collection AND c.archived_utc IS NULL
                  )
                """);
            command.Parameters.AddWithValue("$collection", collectionId.ToString("D"));
            command.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            return command.ExecuteNonQuery() != 0;
        }
    }

    private static void ValidateCollectionId(Guid id)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A nonempty source or collection identifier is required.");
        }
    }

    private static void ValidateCollectionPage(int limit, int offset)
    {
        if (limit is < 1 or > 200 || offset is < 0 or > 1_000_000)
        {
            throw new ArgumentOutOfRangeException(
                limit is < 1 or > 200 ? nameof(limit) : nameof(offset));
        }
    }

    private static (string Display, string Key) NormalizeCollectionName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var display = name.Trim().Normalize(NormalizationForm.FormC);
        var key = display.ToUpperInvariant();
        if (display.Length is < 1 or > 80 || key.Length > 80 ||
            display.Any(char.IsControl))
        {
            throw new ArgumentException("Collection names must be one to eighty printable characters.", nameof(name));
        }

        return (display, key);
    }
}
