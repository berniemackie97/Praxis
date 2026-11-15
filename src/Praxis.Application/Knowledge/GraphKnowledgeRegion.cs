namespace Praxis.Application.Knowledge;

/// <summary>
/// One region of the brain derived from the owner's own structure: a repository, folder or a root's
/// loose files (top level), or a subfolder or project inside one (child). Counts are authorized,
/// scoped and current; <c>PendingSources</c> are captured sources whose understanding is still
/// queued, so a region can be shown as forming rather than empty. Hubs are its best-connected
/// entities, never runtime glue.
/// </summary>
public sealed record GraphKnowledgeRegion(
    string RegionId, string? ParentId, string Label, string Kind, string? Summary,
    int Entities, int Sources, int PendingSources, int Relations, IReadOnlyList<GraphNode> Hubs);
