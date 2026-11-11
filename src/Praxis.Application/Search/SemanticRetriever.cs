using Praxis.Application.Inference;
using Praxis.Domain.Search;

namespace Praxis.Application.Search;

/// <summary>Finds the segments most similar to a query with the model that built the index.</summary>
public sealed class SemanticRetriever(ISemanticVectorStore vectors)
{
    /// <summary>Exact search stays within this many stored vectors; larger indexes need an approximate index.</summary>
    public const int MaximumScannedVectors = 5_000;

    public async Task<IReadOnlyList<SemanticSearchHit>> QueryAsync(
        string query, IEmbeddingModel model, int limit = 100, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentNullException.ThrowIfNull(model);
        if (query.Length > 256 || query.Any(char.IsControl) || limit is < 1 or > 100)
        {
            throw new ArgumentException("The semantic query or result limit is invalid.");
        }

        string digest = EmbeddingVectors.ValidateModelDigest((await model.DescribeAsync(cancellationToken)).Digest);
        if (!vectors.HasVectors(digest))
        {
            return [];
        }

        IReadOnlyList<float[]> embedded = await model.EmbedAsync([query], cancellationToken);
        if (embedded is null || embedded.Count != 1)
        {
            throw new InvalidDataException("The embedding model returned no query vector.");
        }

        float[] queryVector = EmbeddingVectors.Normalize(embedded[0]);
        if (EmbeddingVectors.ValidateModelDigest((await model.DescribeAsync(cancellationToken)).Digest) != digest)
        {
            throw new InvalidDataException("The embedding model changed during retrieval; rebuild the index.");
        }

        return vectors.FindNearest(digest, queryVector, limit, MaximumScannedVectors, cancellationToken);
    }
}
