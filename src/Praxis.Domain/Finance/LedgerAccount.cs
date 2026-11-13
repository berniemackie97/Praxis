namespace Praxis.Domain.Finance;

/// <summary>An owned account across all documents, with its latest proven balance when one exists.</summary>
public sealed record LedgerAccount(
    string AccountKey, string Kind, string? Mask, string? Institution, string Currency, string DisplayName,
    int TransactionCount, DateOnly? FirstPostedOn, DateOnly? LastPostedOn,
    MonetaryAmount? LatestBalance, DateOnly? LatestBalanceOn, FinancialEvidence? LatestBalanceEvidence);
