namespace Praxis.Domain.Finance;

/// <summary>
/// One reconciled transaction. Overlapping documents describing the same activity are merged;
/// <see cref="Evidence"/> lists every corroborating row, the first being the representative.
/// Merchant and category come from model annotations (with their tier) when present.
/// </summary>
public sealed record LedgerTransaction(
    string TransactionId, string AccountKey, DateOnly PostedOn, MonetaryAmount Amount, string Description,
    string? Merchant, string Category, string? CategoryTier, string Status, IReadOnlyList<FinancialEvidence> Evidence);
