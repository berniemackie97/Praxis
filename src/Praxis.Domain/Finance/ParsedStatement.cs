namespace Praxis.Domain.Finance;

/// <summary>One account period within a document and the proof that its rows are complete.</summary>
public sealed record ParsedStatement(
    int Ordinal, string AccountKey, DateOnly? PeriodStart, DateOnly? PeriodEnd,
    MonetaryAmount? OpeningBalance, MonetaryAmount? ClosingBalance, string Verification, string? VerificationNote);
