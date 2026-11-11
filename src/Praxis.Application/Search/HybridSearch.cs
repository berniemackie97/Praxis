using Praxis.Application.Inference;
using Praxis.Domain.Search;

namespace Praxis.Application.Search;

/// <summary>
/// Combines independently ranked lexical and semantic source matches without treating a
/// model similarity score as permission, truth or evidence of an answer.
/// </summary>
public sealed class HybridSearch(ITextSearchIndex lexical, SemanticRetriever semantic)
{
    private const int PoolSize = 100;
    private const double RankOffset = 60;

    public async Task<HybridSearchResults> QueryAsync(
        string query, IEmbeddingModel? model = null, int limit = 20,
        CancellationToken cancellationToken = default, Guid? collectionId = null)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (model is null)
        {
            return new HybridSearchResults(lexical.RetainCurrent(lexical.Query(query, limit, collectionId), collectionId), false);
        }

        IReadOnlyList<SemanticSearchHit> vectors =
            await semantic.QueryAsync(query, model, PoolSize, cancellationToken, collectionId);
        // A source may be revoked or revised during the awaited model call. Read lexical
        // evidence only after the await so the fusion never resurrects an earlier result.
        IReadOnlyList<SearchHit> keyword = lexical.Query(query, PoolSize, collectionId);
        if (vectors.Count == 0)
        {
            return new HybridSearchResults(lexical.RetainCurrent(keyword.Take(limit).ToArray(), collectionId), false);
        }

        // Only currently eligible vectors may influence the fused ranking.
        IReadOnlyList<SearchHit> currentVectors = lexical.RetainCurrent(
            vectors.Select(vector => vector.Hit).ToArray(), collectionId);
        if (currentVectors.Count == 0)
        {
            return new HybridSearchResults(lexical.RetainCurrent(keyword.Take(limit).ToArray(), collectionId), false);
        }

        Dictionary<Guid, (SearchHit Hit, double Score)> combined = new();
        for (int index = 0; index < keyword.Count; index++)
        {
            SearchHit hit = keyword[index];
            combined[hit.ItemId] = (hit, 1d / (RankOffset + index + 1));
        }

        for (int index = 0; index < currentVectors.Count; index++)
        {
            SearchHit hit = currentVectors[index];
            double score = 1d / (RankOffset + index + 1);
            if (combined.TryGetValue(hit.ItemId, out var existing))
            {
                combined[hit.ItemId] = (existing.Hit, existing.Score + score);
            }
            else
            {
                combined[hit.ItemId] = (hit, score);
            }
        }

        SearchHit[] ranked = combined.Values
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Hit.SourceLocator, StringComparer.Ordinal)
            .ThenBy(entry => entry.Hit.ItemId)
            .Take(limit).Select(entry => entry.Hit).ToArray();

        // The two source lists were read at different times. A revocation or revision
        // may occur after the first read; only canonical evidence may cross this boundary.
        return new HybridSearchResults(lexical.RetainCurrent(ranked, collectionId), true);
    }
}
