namespace Praxis.Application.Knowledge;

/// <summary>
/// The whole brain at one knowledge revision: top regions with their children, the evidence links
/// between top regions, and how much authorized knowledge sits outside any region.
/// </summary>
public sealed record GraphRegionMap(
    long Revision, int Entities, int UnplacedEntities, IReadOnlyList<GraphKnowledgeRegion> Regions,
    IReadOnlyList<GraphRegionLink> Links);
