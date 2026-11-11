namespace Praxis.Domain.Knowledge;

public sealed record EntityMention(
    Guid ProposalId, Guid SourceId, Guid ItemId, int Revision, string? MentionType,
    string Evidence, int EvidenceOffset, string Status, string SourceName, string? Verdict);
