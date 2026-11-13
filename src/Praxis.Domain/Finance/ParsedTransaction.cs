namespace Praxis.Domain.Finance;

/// <summary>
/// One row exactly as a document states it, with the locator that cites it (for example
/// <c>row:14</c>, <c>stmttrn:3</c> or <c>page:2:line:31</c>). Provider fields are facts;
/// categories and merchants are derived later.
/// </summary>
public sealed record ParsedTransaction(
    int Ordinal, string AccountKey, int? StatementOrdinal, DateOnly PostedOn, MonetaryAmount Amount,
    string Description, string Status, string? ProviderType, string? ProviderId, string? CheckNumber,
    string? ProviderCategory, MonetaryAmount? BalanceAfter, string Locator, string Origin);
