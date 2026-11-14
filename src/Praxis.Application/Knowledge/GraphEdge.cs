namespace Praxis.Application.Knowledge;

public sealed record GraphEdge(
    Guid RelationId, Guid SubjectId, Guid ObjectId, string Predicate,
    string Origin, string EvidenceStatus, GraphEvidence? Evidence,
    IReadOnlyList<string> Actions);
