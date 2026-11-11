using Microsoft.Data.Sqlite;
using Praxis.Domain.Library;
using Praxis.Infrastructure.Persistence;

namespace Praxis.Infrastructure.Library;

/// <summary>Read model of the latest revision of each item with its text-processing state.</summary>
public sealed class LocalLibrary
{
    private readonly CatalogDatabase database;

    internal LocalLibrary(CatalogDatabase database) => this.database = database;

    public IReadOnlyList<LibraryItem> List(int limit = 100, int offset = 0, Guid? collectionId = null)
    {
        if (limit is < 1 or > 200 || offset < 0)
        {
            throw new ArgumentOutOfRangeException(limit is < 1 or > 200 ? nameof(limit) : nameof(offset));
        }

        if (collectionId == Guid.Empty)
        {
            throw new ArgumentException("A nonempty collection identifier is required.", nameof(collectionId));
        }

        return database.Execute(connection =>
        {
            using SqliteCommand command = connection.CreateCommand("""
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
            command.Parameters.AddWithValue("$collection", collectionId.HasValue ? (object)collectionId.Value.ToString("D") : DBNull.Value);
            using SqliteDataReader reader = command.ExecuteReader();
            List<LibraryItem> result = new();
            while (reader.Read())
            {
                result.Add(new LibraryItem(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetInt32(5), reader.GetString(6), reader.GetInt64(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9)));
            }

            return result;
        });
    }
}
