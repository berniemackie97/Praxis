using Praxis.Domain.Search;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>Search hits with the retrieval mode used and an optional owner-facing notice.</summary>
public sealed record SearchResponse(IReadOnlyList<SearchHit> Hits, string Mode, string? Notice);
