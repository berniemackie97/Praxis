using Praxis.Application.Inference;

namespace Praxis.Infrastructure.Search;

public sealed record HybridSearchResults(IReadOnlyList<LocalSearchHit> Hits, bool UsedEmbeddings);

/// <summary>
/// Combines independently ranked lexical and semantic source matches without treating a
/// model similarity score as permission, truth or evidence of an answer.
/// </summary>
public sealed class LocalHybridSearch(LocalTextSearch lexical, LocalSemanticIndex semantic)
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
        IReadOnlyList<LocalSearchHit> keyword = lexical.Query(query, PoolSize);
        if (vectors.Count == 0)
        {
            return new HybridSearchResults(keyword.Take(limit).ToArray(), false);
        }

        Dictionary<Guid, (LocalSearchHit Hit, double Score)> combined = new();
        for (int index = 0; index < keyword.Count; index++)
        {
            LocalSearchHit hit = keyword[index];
            combined[hit.ItemId] = (hit, 1d / (RankOffset + index + 1));
        }

        for (int index = 0; index < vectors.Count; index++)
        {
            LocalSearchHit hit = vectors[index].Hit;
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
