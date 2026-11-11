using Praxis.Application.Inference;
using Praxis.Domain.Search;

namespace Praxis.Application.Search;

/// <summary>
/// Embeds current text segments with a local model and stores the vectors. Network inference
/// happens outside any database transaction, and results are discarded if the model changes.
/// </summary>
public sealed class SemanticIndexer(ISemanticVectorStore vectors)
{
    public const int MaximumSegmentsPerRefresh = 2_048;
    private const int BatchSize = 8;

    /// <summary>
    /// Incrementally indexes a bounded set of unindexed segments, or atomically rebuilds a
    /// complete bounded index.
    /// </summary>
    public async Task<SemanticIndexUpdate> RefreshAsync(
        IEmbeddingModel model, bool rebuild = false, int maxSegments = 512,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (maxSegments is < 1 or > MaximumSegmentsPerRefresh)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSegments));
        }

        string digest = EmbeddingVectors.ValidateModelDigest((await model.DescribeAsync(cancellationToken)).Digest);
        List<SemanticSegment> segments = vectors.ReadSegmentsToIndex(digest, rebuild, maxSegments + 1).ToList();
        bool hasMore = segments.Count > maxSegments;
        if (hasMore && rebuild)
        {
            throw new InvalidOperationException("The complete semantic rebuild exceeds its segment budget.");
        }

        if (hasMore)
        {
            segments.RemoveAt(segments.Count - 1);
        }

        List<SegmentEmbedding> embeddings = new(segments.Count);
        int dimensions = 0;
        for (int start = 0; start < segments.Count; start += BatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SemanticSegment[] batch = segments.Skip(start).Take(BatchSize).ToArray();
            IReadOnlyList<float[]> output = await model.EmbedAsync(
                batch.Select(segment => segment.Text).ToArray(), cancellationToken);
            if (output is null || output.Count != batch.Length)
            {
                throw new InvalidDataException("The embedding model returned an incomplete batch.");
            }

            for (int index = 0; index < batch.Length; index++)
            {
                float[] vector = EmbeddingVectors.Normalize(output[index]);
                if (dimensions != 0 && dimensions != vector.Length)
                {
                    throw new InvalidDataException("The embedding model changed vector dimensions within one index.");
                }

                dimensions = vector.Length;
                embeddings.Add(new SegmentEmbedding(batch[index], vector));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (EmbeddingVectors.ValidateModelDigest((await model.DescribeAsync(cancellationToken)).Digest) != digest)
        {
            throw new InvalidDataException("The embedding model changed during indexing; retry with a stable model.");
        }

        return new SemanticIndexUpdate(vectors.Store(digest, rebuild, embeddings), hasMore);
    }
}
