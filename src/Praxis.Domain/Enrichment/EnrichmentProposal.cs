namespace Praxis.Domain.Enrichment;

public sealed record EnrichmentProposal(
    Guid ProposalId, Guid SourceId, Guid ItemId, int Revision, string SourceSha256,
    string Producer, string Kind, string? EntityType, string Value, string Evidence,
    int EvidenceOffset, string Status, string? Correction,
    string? RelationSubject, string? RelationObject, string? Flags);
