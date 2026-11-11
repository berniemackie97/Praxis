using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using Praxis.Application.Inference;
using Praxis.Domain.Search;
using Praxis.Infrastructure.Catalog;

namespace Praxis.Infrastructure.Search;

/// <summary>
/// A derived, model-versioned index over current active canonical text segments. No source
/// authority lives here; every returned vector is joined against the current eligible evidence.
/// </summary>
public sealed class LocalSemanticIndex
{
    private readonly CatalogDatabase database;

    internal LocalSemanticIndex(CatalogDatabase database) => this.database = database;

    private const int MaximumScanRows = 5_000;
    private sealed record Target(Guid ItemId, int Revision, int Ordinal, string Digest, string Text);
    private sealed record PendingVector(Target Target, byte[] Bytes, int Dimensions);

    /// <summary>
    /// Incrementally indexes a bounded set of unindexed segments, or atomically rebuilds a
    /// complete bounded index. Network inference occurs outside the SQLite transaction.
    /// </summary>
    public async Task<SemanticIndexUpdate> RefreshAsync(
        IEmbeddingModel model, bool rebuild = false, int maxSegments = 512,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (maxSegments is < 1 or > 2_048)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSegments));
        }

        string digest = ValidateModel((await model.DescribeAsync(cancellationToken)).Digest);
        List<Target> targets = database.Execute(connection =>
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
            query.Parameters.AddWithValue("$model", digest);
            query.Parameters.AddWithValue("$rebuild", rebuild ? 1 : 0);
            query.Parameters.AddWithValue("$limit", maxSegments + 1);
            using SqliteDataReader reader = query.ExecuteReader();
            List<Target> records = new();
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

                records.Add(new Target(Guid.Parse(reader.GetString(0)),
                    reader.GetInt32(1), reader.GetInt32(2), reader.GetString(3), content));
            }

            return records;
        });

        bool hasMore = targets.Count > maxSegments;
        if (hasMore && rebuild)
        {
            throw new InvalidOperationException("The complete semantic rebuild exceeds its segment budget.");
        }

        if (hasMore)
        {
            targets.RemoveAt(targets.Count - 1);
        }

        List<PendingVector> pending = new(targets.Count);
        int dimensions = 0;
        for (int start = 0; start < targets.Count; start += 8)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Target[] batch = targets.Skip(start).Take(8).ToArray();
            IReadOnlyList<float[]> output = await model.EmbedAsync(
                batch.Select(target => target.Text).ToArray(), cancellationToken);
            if (output is null || output.Count != batch.Length)
            {
                throw new InvalidDataException("The embedding model returned an incomplete batch.");
            }

            for (int index = 0; index < batch.Length; index++)
            {
                byte[] bytes = Encode(output[index], out int count);
                if (dimensions != 0 && dimensions != count)
                {
                    throw new InvalidDataException("The embedding model changed vector dimensions within one index.");
                }

                dimensions = count;
                pending.Add(new PendingVector(batch[index], bytes, count));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (ValidateModel((await model.DescribeAsync(cancellationToken)).Digest) != digest)
        {
            throw new InvalidDataException("The embedding model changed during indexing; retry with a stable model.");
        }

        int inserted = database.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            if (rebuild)
            {
                using SqliteCommand clear = connection.CreateCommand("DELETE FROM semantic_vectors");
                clear.Transaction = transaction;
                clear.ExecuteNonQuery();
            }

            int count = 0;
            foreach (PendingVector entry in pending)
            {
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
                insert.Parameters.AddWithValue("$item", entry.Target.ItemId.ToString("D"));
                insert.Parameters.AddWithValue("$revision", entry.Target.Revision);
                insert.Parameters.AddWithValue("$ordinal", entry.Target.Ordinal);
                insert.Parameters.AddWithValue("$model", digest);
                insert.Parameters.AddWithValue("$source", entry.Target.Digest);
                insert.Parameters.AddWithValue("$dimensions", entry.Dimensions);
                insert.Parameters.AddWithValue("$vector", entry.Bytes);
                insert.Parameters.AddWithValue("$text", entry.Target.Text);
                insert.Parameters.AddWithValue("$now", CatalogValues.Timestamp(DateTimeOffset.UtcNow));
                count += insert.ExecuteNonQuery();
            }

            transaction.Commit();
            return count;
        });
        return new SemanticIndexUpdate(inserted, hasMore);
    }

    internal async Task<IReadOnlyList<SemanticSearchHit>> QueryAsync(
        string query, IEmbeddingModel model, int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(model);
        if (query.Length > 256 || query.Any(char.IsControl) || limit is < 1 or > 100)
        {
            throw new ArgumentException("The semantic query or result limit is invalid.");
        }

        string digest = ValidateModel((await model.DescribeAsync(cancellationToken)).Digest);
        bool hasVectors = database.Execute(connection =>
        {
            using SqliteCommand check = connection.CreateCommand("""
                                                                 SELECT EXISTS(
                                                                     SELECT 1 FROM semantic_vectors v
                                                                     JOIN eligible_semantic_segments e
                                                                       ON e.item_id = v.item_id AND e.revision = v.revision
                                                                       AND e.ordinal = v.ordinal AND e.source_digest = v.source_digest
                                                                     WHERE v.model_digest = $model LIMIT 1)
                                                                 """);
            check.Parameters.AddWithValue("$model", digest);
            return Convert.ToInt64(check.ExecuteScalar()) != 0;
        });
        if (!hasVectors)
        {
            return [];
        }

        IReadOnlyList<float[]> embedded = await model.EmbedAsync([query], cancellationToken);
        if (embedded is null || embedded.Count != 1)
        {
            throw new InvalidDataException("The embedding model returned no query vector.");
        }

        float[] queryVector = Normalize(embedded[0]);
        if (ValidateModel((await model.DescribeAsync(cancellationToken)).Digest) != digest)
        {
            throw new InvalidDataException("The embedding model changed during retrieval; rebuild the index.");
        }

        return database.Execute(connection =>
        {
            using SqliteCommand read = connection.CreateCommand("""
                                                                SELECT e.source_id, e.item_id, e.revision, e.locator,
                                                                       e.source_digest, v.segment_text, v.vector, v.dimensions
                                                                FROM semantic_vectors v
                                                                JOIN eligible_semantic_segments e
                                                                    ON e.item_id = v.item_id AND e.revision = v.revision
                                                                    AND e.ordinal = v.ordinal AND e.source_digest = v.source_digest
                                                                WHERE v.model_digest = $model
                                                                ORDER BY e.item_id, e.ordinal
                                                                LIMIT $limit
                                                                """);
            read.CommandTimeout = 5;
            read.Parameters.AddWithValue("$model", digest);
            read.Parameters.AddWithValue("$limit", MaximumScanRows + 1);
            using SqliteDataReader reader = read.ExecuteReader();
            Dictionary<Guid, SemanticSearchHit> strongest = new();
            int examined = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++examined > MaximumScanRows)
                {
                    throw new InvalidOperationException("The semantic index exceeds the bounded exact-search budget.");
                }

                int dimensions = reader.GetInt32(7);
                byte[] stored = reader.GetFieldValue<byte[]>(6);
                if (dimensions != queryVector.Length || stored.Length != dimensions * sizeof(float))
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

                    similarity += queryVector[dimension] * (double)value;
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
    }

    private static string ValidateModel(string digest) =>
        string.IsNullOrWhiteSpace(digest) || digest.Length > 256 || digest.Any(char.IsControl)
            ? throw new InvalidDataException("The embedding model identity is invalid.")
            : digest;

    private static byte[] Encode(float[] values, out int count)
    {
        float[] normalized = Normalize(values);
        count = normalized.Length;
        byte[] result = new byte[count * sizeof(float)];
        for (int index = 0; index < count; index++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(
                result.AsSpan(index * sizeof(float), sizeof(float)), normalized[index]);
        }

        return result;
    }

    private static float[] Normalize(float[]? values)
    {
        if (values is null || values.Length is < 1 or > 4_096 || values.Any(value => !float.IsFinite(value)))
        {
            throw new InvalidDataException("The embedding model produced invalid vector values.");
        }

        double squared = values.Sum(value => (double)value * value);
        if (!double.IsFinite(squared) || squared <= 0)
        {
            throw new InvalidDataException("The embedding model returned a zero-length vector.");
        }

        float scale = (float)(1d / Math.Sqrt(squared));
        return values.Select(value => value * scale).ToArray();
    }
}
