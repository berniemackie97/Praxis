namespace Praxis.Application.Knowledge;

public sealed record GraphEvidence(
    Guid ProposalId, Guid SourceId, Guid ItemId, int Revision, string SourceDigest,
    string Locator, string Text, int Offset, string Status, string? Verdict);
