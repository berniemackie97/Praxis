using System.Text;
using Microsoft.Data.Sqlite;
using Praxis.Domain.Collections;
using Praxis.Infrastructure.Persistence;

namespace Praxis.Infrastructure.Collections;

/// <summary>Owner-authored named collections with explicit many-to-many source membership.</summary>
public sealed class LocalCollections
{
    private readonly CatalogDatabase database;

    internal LocalCollections(CatalogDatabase database) => this.database = database;

    public SourceCollection Create(string name)
    {
        (string display, string key) = NormalizeCollectionName(name);
        Guid id = Guid.NewGuid();
        database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
                                                                   INSERT INTO source_collections (collection_id, name, name_key, created_utc)
                                                                   VALUES ($id, $name, $key, $now)
                                                                   """);
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$name", display);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$now", CatalogValues.Timestamp(DateTimeOffset.UtcNow));
            try
            {
                command.ExecuteNonQuery();
            }
            catch (SqliteException exception) when (exception.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException("A collection with this name already exists.", exception);
            }
        });

        return new SourceCollection(id, display, 0);
    }

    public bool Rename(Guid collectionId, string name)
    {
        ValidateCollectionId(collectionId);
        (string display, string key) = NormalizeCollectionName(name);
        return database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
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
        });
    }

    public bool Archive(Guid collectionId)
    {
        ValidateCollectionId(collectionId);
        return database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
                                                                   UPDATE source_collections SET archived_utc = $now
                                                                   WHERE collection_id = $id AND archived_utc IS NULL
                                                                   """);
            command.Parameters.AddWithValue("$id", collectionId.ToString("D"));
            command.Parameters.AddWithValue("$now", CatalogValues.Timestamp(DateTimeOffset.UtcNow));
            return command.ExecuteNonQuery() != 0;
        });
    }

    public IReadOnlyList<SourceCollection> List(int limit = 100, int offset = 0)
    {
        ValidateCollectionPage(limit, offset);
        return database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
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
            using SqliteDataReader reader = command.ExecuteReader();
            List<SourceCollection> rows = new();
            while (reader.Read())
            {
                rows.Add(new SourceCollection(Guid.Parse(reader.GetString(0)), reader.GetString(1), checked((int)reader.GetInt64(2))));
            }

            return rows;
        });
    }

    public IReadOnlyList<Guid> ListForSource(Guid sourceId, int limit = 100)
    {
        ValidateCollectionId(sourceId);
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
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
            using SqliteDataReader reader = command.ExecuteReader();
            List<Guid> rows = new();
            while (reader.Read())
            {
                rows.Add(Guid.Parse(reader.GetString(0)));
            }

            return rows;
        });
    }

    public bool AddSource(Guid collectionId, Guid sourceId)
    {
        ValidateCollectionId(collectionId);
        ValidateCollectionId(sourceId);
        return database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
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
            command.Parameters.AddWithValue("$now", CatalogValues.Timestamp(DateTimeOffset.UtcNow));
            return command.ExecuteNonQuery() != 0;
        });
    }

    public bool RemoveSource(Guid collectionId, Guid sourceId)
    {
        ValidateCollectionId(collectionId);
        ValidateCollectionId(sourceId);
        return database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
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
        });
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
            throw new ArgumentOutOfRangeException(limit is < 1 or > 200 ? nameof(limit) : nameof(offset));
        }
    }

    private static (string Display, string Key) NormalizeCollectionName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string display = name.Trim().Normalize(NormalizationForm.FormC);
        string key = display.ToUpperInvariant();
        if (display.Length is < 1 or > 80 || key.Length > 80 || display.Any(char.IsControl))
        {
            throw new ArgumentException("Collection names must be one to eighty printable characters.", nameof(name));
        }

        return (display, key);
    }
}
