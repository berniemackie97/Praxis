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
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (model is null)
        {
            return new HybridSearchResults(lexical.Query(query, limit), false);
        }

        IReadOnlyList<SemanticSearchHit> vectors =
            await semantic.QueryAsync(query, model, PoolSize, cancellationToken);
        // A source may be revoked or revised during the awaited model call. Read lexical
        // evidence only after the await so the fusion never resurrects an earlier result.
        IReadOnlyList<SearchHit> keyword = lexical.Query(query, PoolSize);
        if (vectors.Count == 0)
        {
            return new HybridSearchResults(keyword.Take(limit).ToArray(), false);
        }

        Dictionary<Guid, (SearchHit Hit, double Score)> combined = new();
        for (int index = 0; index < keyword.Count; index++)
        {
            SearchHit hit = keyword[index];
            combined[hit.ItemId] = (hit, 1d / (RankOffset + index + 1));
        }

        for (int index = 0; index < vectors.Count; index++)
        {
            SearchHit hit = vectors[index].Hit;
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

        return new HybridSearchResults(combined.Values
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Hit.SourceLocator, StringComparer.Ordinal)
            .ThenBy(entry => entry.Hit.ItemId)
            .Take(limit).Select(entry => entry.Hit).ToArray(), true);
    }
}
