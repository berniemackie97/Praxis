namespace Praxis.Application.Knowledge;

/// <summary>The previously promoted project-typed entities, with current scoped provenance.</summary>
public sealed record GraphClassificationAuditPage(int ProjectTypedCount,
    IReadOnlyList<GraphClassificationAuditEntry> Entries, string? NextCursor, long Revision);
