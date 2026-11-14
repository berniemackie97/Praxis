namespace Praxis.Application.Knowledge;

public sealed record GraphNode(
    Guid EntityId, string Label, string? EntityType, string TypeOrigin, string Status,
    int Mentions, int Sources, bool TypeConflict, GraphEvidence? Evidence,
    IReadOnlyList<string> Actions);
