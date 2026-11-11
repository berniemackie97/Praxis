namespace Praxis.Domain.Verification;

public sealed record ClaimedVerification(
    Guid RequestId, Guid LeaseKey, VerificationProvider Provider, string Payload, IReadOnlySet<int> Ordinals);
