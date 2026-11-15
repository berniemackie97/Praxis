namespace Praxis.Application.Knowledge;

public sealed record GraphZoneOverview(int EntityCount, int RelationshipCount,
    IReadOnlyList<GraphTypeCount> Zones, IReadOnlyList<GraphZoneLink> Links);
