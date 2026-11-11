namespace Praxis.Domain.Enrichment;

/// <summary>
/// A proposed assertion and the exact span of extracted text that supports it. For a relation,
/// <see cref="Value"/> is the predicate and the subject and object are entity names.
/// </summary>
public sealed record ProposalCandidate(
    string Kind, string? EntityType, string Value, int EvidenceOffset, int EvidenceLength,
    string? RelationSubject = null, string? RelationObject = null, string? Flags = null);
