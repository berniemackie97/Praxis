namespace Praxis.Domain.Search;

public sealed record HybridSearchResults(IReadOnlyList<SearchHit> Hits, bool UsedEmbeddings);
