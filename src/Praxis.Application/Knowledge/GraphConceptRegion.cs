namespace Praxis.Application.Knowledge;

/// <summary>A source-visible project anchor, not a guessed topic or a claim of shared ownership.</summary>
public sealed record GraphConceptRegion(Guid RegionId, string Label, int RelationCount,
    int RelatedEntityCount, int ContainedEntityCount);
