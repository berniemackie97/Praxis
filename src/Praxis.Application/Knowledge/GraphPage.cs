namespace Praxis.Application.Knowledge;

public sealed record GraphPage(
    string Outcome, Guid? FocusId, IReadOnlyList<GraphNode> Nodes,
    IReadOnlyList<GraphEdge> Edges, string? NextCursor);
