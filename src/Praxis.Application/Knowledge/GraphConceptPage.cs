namespace Praxis.Application.Knowledge;

/// <summary>Paged root or child groups in one authorized scope and catalog revision.</summary>
public sealed record GraphConceptPage(int RegionCount,
    IReadOnlyList<GraphConceptRegion> Regions, string? NextCursor, long Revision);
