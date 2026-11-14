namespace Praxis.Domain.Enrichment;

/// <summary>
/// A grounded suggestion for a source. <see cref="Confirmed"/> is true when it counts as knowledge:
/// the owner accepted it, a verifier supported it, or the strong or strongest tier produced it and
/// code verified it. Unconfirmed items are the local model's suggestions.
/// </summary>
public sealed record EnrichmentProposal(
    Guid ProposalId, Guid SourceId, Guid ItemId, int Revision, string SourceSha256,
    string Producer, string Kind, string? EntityType, string Value, string Evidence,
    int EvidenceOffset, string Status, string? Correction,
    string? RelationSubject, string? RelationObject, string? Flags, bool Confirmed = false);
