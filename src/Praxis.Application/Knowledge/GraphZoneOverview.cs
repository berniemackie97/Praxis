namespace Praxis.Application.Knowledge;

public sealed record GraphZoneLink(string SubjectType, string ObjectType, int ConfirmedCount,
    int SuggestedCount, int ContradictedCount);

public sealed record GraphZoneOverview(int EntityCount, int RelationshipCount,
    IReadOnlyList<GraphTypeCount> Zones, IReadOnlyList<GraphZoneLink> Links);
