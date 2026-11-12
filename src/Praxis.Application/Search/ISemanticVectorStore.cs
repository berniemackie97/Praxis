using Praxis.Domain.Search;

namespace Praxis.Application.Search;

/// <summary>
/// A derived, model-versioned vector index over current active canonical text segments. No
/// source authority lives here; every returned vector is joined against current eligible evidence.
/// </summary>
public interface ISemanticVectorStore
{
    /// <summary>
    /// Up to <paramref name="limit"/> eligible segments without a current vector for the model,
    /// or every eligible segment when <paramref name="rebuild"/> is true.
    /// </summary>
    IReadOnlyList<SemanticSegment> ReadSegmentsToIndex(string modelDigest, bool rebuild, int limit);

    /// <summary>
    /// Stores embeddings of segments that are still eligible, replacing every stored vector first
    /// when <paramref name="rebuild"/> is true, in one transaction. Returns the number stored.
    /// </summary>
    int Store(string modelDigest, bool rebuild, IReadOnlyList<SegmentEmbedding> embeddings);

    bool HasVectors(string modelDigest, Guid? collectionId = null);

    /// <summary>
    /// Exact nearest-neighbor search: the strongest eligible segment per item, most similar first.
    /// Throws when the index holds more than <paramref name="maximumScanned"/> vectors for the model.
    /// </summary>
    IReadOnlyList<SemanticSearchHit> FindNearest(
        string modelDigest, float[] unitQuery, int limit, int maximumScanned, CancellationToken cancellationToken, Guid? collectionId = null);
}
