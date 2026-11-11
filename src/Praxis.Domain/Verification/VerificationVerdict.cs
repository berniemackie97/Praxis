namespace Praxis.Domain.Verification;

public sealed record VerificationVerdict(int Ordinal, string Verdict, string? CorrectedType, string? Note);
