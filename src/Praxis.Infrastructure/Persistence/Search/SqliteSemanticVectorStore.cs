using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using Praxis.Application.Search;
using Praxis.Domain.Search;

namespace Praxis.Infrastructure.Persistence.Search;

/// <summary>
/// SQLite storage of model-versioned segment embeddings as little-endian float32 blobs. Reads
/// join every vector against the current eligible segments, so revoked or superseded text is
/// never returned.
/// </summary>
public sealed class SqliteSemanticVectorStore : ISemanticVectorStore
{
    private readonly CatalogDatabase database;
    private readonly TimeProvider clock;

    internal SqliteSemanticVectorStore(CatalogDatabase database, TimeProvider clock)
    {
        this.database = database;
        this.clock = clock;
    }

    public IReadOnlyList<SemanticSegment> ReadSegmentsToIndex(string modelDigest, bool rebuild, int limit) => database.Execute(connection =>
    {
        using SqliteCommand query = connection.CreateCommand("""
                                                             SELECT e.item_id, e.revision, e.ordinal, e.source_digest,
                                                                    e.content, e.start_offset, e.length
                                                             FROM eligible_semantic_segments e
                                                             LEFT JOIN semantic_vectors v ON v.item_id = e.item_id
                                                                 AND v.revision = e.revision AND v.ordinal = e.ordinal
                                                                 AND v.model_digest = $model AND v.source_digest = e.source_digest
                                                             WHERE $rebuild = 1 OR v.item_id IS NULL
                                                             ORDER BY e.item_id, e.revision, e.ordinal
                                                             LIMIT $limit
                                                             """);
        query.CommandTimeout = 30;
        query.Parameters.AddWithValue("$model", modelDigest);
        query.Parameters.AddWithValue("$rebuild", rebuild ? 1 : 0);
        query.Parameters.AddWithValue("$limit", limit);
        using SqliteDataReader reader = query.ExecuteReader();
        List<SemanticSegment> segments = new();
        while (reader.Read())
        {
            string text = reader.GetString(4);
            int start = reader.GetInt32(5);
            int length = reader.GetInt32(6);
            if (start < 0 || length is < 1 or > 2_000 || start > text.Length - length)
            {
                throw new InvalidDataException("A text segment has invalid canonical offsets.");
            }

            string content = text.Substring(start, length);
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidDataException("An indexed segment has no text.");
            }

            segments.Add(new SemanticSegment(Guid.Parse(reader.GetString(0)),
                reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3), content));
        }

        return segments;
    });

    public int Store(string modelDigest, bool rebuild, IReadOnlyList<SegmentEmbedding> embeddings)
    {
        ArgumentNullException.ThrowIfNull(embeddings);
        string now = CatalogValues.Timestamp(clock.GetUtcNow());
        return database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            if (rebuild)
            {
                using SqliteCommand clear = connection.CreateCommand("DELETE FROM semantic_vectors");
                clear.Transaction = transaction;
                clear.ExecuteNonQuery();
            }

            int count = 0;
            foreach (SegmentEmbedding embedding in embeddings)
            {
                SemanticSegment segment = embedding.Segment;
                using SqliteCommand insert = connection.CreateCommand("""
                                                                      INSERT INTO semantic_vectors
                                                                          (item_id, revision, ordinal, model_digest, source_digest,
                                                                           dimensions, vector, segment_text, recorded_utc)
                                                                      SELECT $item, $revision, $ordinal, $model, $source,
                                                                             $dimensions, $vector, $text, $now
                                                                      WHERE EXISTS (
                                                                          SELECT 1 FROM eligible_semantic_segments e
                                                                          WHERE e.item_id = $item AND e.revision = $revision
                                                                            AND e.ordinal = $ordinal AND e.source_digest = $source)
                                                                      ON CONFLICT(item_id, revision, ordinal, model_digest)
                                                                      DO UPDATE SET source_digest=excluded.source_digest,
                                                                          dimensions=excluded.dimensions, vector=excluded.vector,
                                                                          segment_text=excluded.segment_text,
                                                                          recorded_utc=excluded.recorded_utc
                                                                      """);
                insert.Transaction = transaction;
                insert.Parameters.AddWithValue("$item", segment.ItemId.ToString("D"));
                insert.Parameters.AddWithValue("$revision", segment.Revision);
                insert.Parameters.AddWithValue("$ordinal", segment.Ordinal);
                insert.Parameters.AddWithValue("$model", modelDigest);
                insert.Parameters.AddWithValue("$source", segment.SourceDigest);
                insert.Parameters.AddWithValue("$dimensions", embedding.Vector.Length);
                insert.Parameters.AddWithValue("$vector", Encode(embedding.Vector));
                insert.Parameters.AddWithValue("$text", segment.Text);
                insert.Parameters.AddWithValue("$now", now);
                count += insert.ExecuteNonQuery();
            }

            transaction.Commit();
            return count;
        });
    }

    public bool HasVectors(string modelDigest, Guid? collectionId = null) => database.ExecuteRead(connection =>
    {
        using SqliteCommand check = connection.CreateCommand("""
                                                             SELECT EXISTS(
                                                                 SELECT 1 FROM semantic_vectors v
                                                                 JOIN eligible_semantic_segments e
                                                                   ON e.item_id = v.item_id AND e.revision = v.revision
                                                                   AND e.ordinal = v.ordinal AND e.source_digest = v.source_digest
                                                                 WHERE v.model_digest = $model
                                                                   AND ($collection IS NULL OR EXISTS (
                                                                                   SELECT 1 FROM source_collection_memberships membership
                                                                                   JOIN source_collections scope ON scope.collection_id = membership.collection_id
                                                                                   WHERE membership.source_id = e.source_id
                                                                                     AND membership.collection_id = $collection
                                                                                     AND scope.archived_utc IS NULL)) LIMIT 1)
                                                             """);
        check.Parameters.AddWithValue("$model", modelDigest);
        check.Parameters.AddWithValue("$collection", (object?)collectionId?.ToString("D") ?? DBNull.Value);
        return Convert.ToInt64(check.ExecuteScalar()) != 0;
    });

    public IReadOnlyList<SemanticSearchHit> FindNearest(
        string modelDigest, float[] unitQuery, int limit, int maximumScanned, CancellationToken cancellationToken,
        Guid? collectionId = null) =>
        database.ExecuteRead(connection =>
        {
            using SqliteCommand read = connection.CreateCommand("""
                                                                SELECT e.source_id, e.item_id, e.revision, e.locator,
                                                                       e.source_digest, v.segment_text, v.vector, v.dimensions
                                                                FROM semantic_vectors v
                                                                JOIN eligible_semantic_segments e
                                                                    ON e.item_id = v.item_id AND e.revision = v.revision
                                                                    AND e.ordinal = v.ordinal AND e.source_digest = v.source_digest
                                                                WHERE v.model_digest = $model
                                                                  AND ($collection IS NULL OR EXISTS (
                                                                                  SELECT 1 FROM source_collection_memberships membership
                                                                                  JOIN source_collections scope ON scope.collection_id = membership.collection_id
                                                                                  WHERE membership.source_id = e.source_id
                                                                                    AND membership.collection_id = $collection
                                                                                    AND scope.archived_utc IS NULL))
                                                                ORDER BY e.item_id, e.ordinal
                                                                LIMIT $limit
                                                                """);
            read.CommandTimeout = 5;
            read.Parameters.AddWithValue("$model", modelDigest);
            read.Parameters.AddWithValue("$collection", (object?)collectionId?.ToString("D") ?? DBNull.Value);
            read.Parameters.AddWithValue("$limit", maximumScanned + 1);
            using SqliteDataReader reader = read.ExecuteReader();
            Dictionary<Guid, SemanticSearchHit> strongest = new();
            int examined = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++examined > maximumScanned)
                {
                    throw new InvalidOperationException("The semantic index exceeds the bounded exact-search budget.");
                }

                int dimensions = reader.GetInt32(7);
                byte[] stored = reader.GetFieldValue<byte[]>(6);
                if (dimensions != unitQuery.Length || stored.Length != dimensions * sizeof(float))
                {
                    throw new InvalidDataException("Embedding dimensions changed; rebuild the semantic index.");
                }

                double similarity = 0;
                for (int dimension = 0; dimension < dimensions; dimension++)
                {
                    float value = BinaryPrimitives.ReadSingleLittleEndian(
                        stored.AsSpan(dimension * sizeof(float), sizeof(float)));
                    if (!float.IsFinite(value))
                    {
                        throw new InvalidDataException("A stored embedding is invalid.");
                    }

                    similarity += unitQuery[dimension] * (double)value;
                }

                Guid item = Guid.Parse(reader.GetString(1));
                string locator = reader.GetString(3);
                string snippet = reader.GetString(5);
                if (snippet.Length > 480)
                {
                    snippet = snippet[..480];
                }

                SearchHit hit = new(Guid.Parse(reader.GetString(0)), item, reader.GetInt32(2),
                    Path.GetFileName(locator), locator, reader.GetString(4), snippet);
                if (!strongest.TryGetValue(item, out SemanticSearchHit? previous) ||
                    similarity > previous.Similarity)
                {
                    strongest[item] = new SemanticSearchHit(hit, similarity);
                }
            }

            return strongest.Values.OrderByDescending(candidate => candidate.Similarity)
                .ThenBy(candidate => candidate.Hit.SourceLocator, StringComparer.Ordinal)
                .ThenBy(candidate => candidate.Hit.ItemId)
                .Take(limit).ToArray();
        });

    private static byte[] Encode(float[] unitVector)
    {
        byte[] result = new byte[unitVector.Length * sizeof(float)];
        for (int index = 0; index < unitVector.Length; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(result.AsSpan(index * sizeof(float), sizeof(float)), unitVector[index]);
        }

        return result;
    }
}
