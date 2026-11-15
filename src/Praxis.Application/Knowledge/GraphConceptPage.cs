namespace Praxis.Application.Knowledge;

/// <summary>Confirmed project anchors in the current evidence scope, paged independently of entity types.</summary>
public sealed record GraphConceptPage(int RegionCount,
    IReadOnlyList<GraphConceptRegion> Regions, string? NextCursor);
