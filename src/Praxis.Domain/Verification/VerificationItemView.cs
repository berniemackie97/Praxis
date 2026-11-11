namespace Praxis.Domain.Verification;

public sealed record VerificationItemView(
    int Ordinal, Guid? ProposalId, Guid? EntityId, bool Included, string? Verdict,
    string? CorrectedType, string? Note);
