namespace Praxis.Application.Desktop.Contracts;

/// <summary>
/// The knowledge revision (moves whenever anything the graph can show changes) and the newest
/// revision log id. Clients poll this cheaply and refresh only what changed.
/// </summary>
public sealed record KnowledgeWatermarkResponse(long GraphRevision, long LatestRevisionId);
