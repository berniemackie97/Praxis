using Praxis.Domain.Knowledge;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>Revisions Praxis made to or proposes for its own classifications, newest first.</summary>
public sealed record KnowledgeRevisionsResponse(IReadOnlyList<KnowledgeRevision> Revisions);
