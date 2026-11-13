namespace Praxis.Domain.Finance;

/// <summary>
/// An account as identified by one document. <see cref="AccountKey"/> is derived from the last
/// four account digits and the account family so exports in different formats meet; the full
/// account number is never stored.
/// </summary>
public sealed record FinancialAccount(string AccountKey, string Kind, string? Mask, string? Institution, string Currency, string DisplayName);
