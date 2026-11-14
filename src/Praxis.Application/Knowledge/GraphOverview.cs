namespace Praxis.Application.Knowledge;

/// <summary>Counts of currently visible entities in one canonical, source-scoped catalog.</summary>
public sealed record GraphOverview(int EntityCount, IReadOnlyList<GraphTypeCount> Types);

public sealed record GraphTypeCount(string EntityType, int Count);
