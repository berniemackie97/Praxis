using Praxis.Domain.Finance;

namespace Praxis.Application.Finance;

/// <summary>
/// A filter over reconciled transactions. Direction is <c>inflow</c>, <c>outflow</c> or null for
/// both; status is <c>posted</c>, <c>pending</c> or null for both. Text matches description or merchant.
/// </summary>
public sealed record LedgerQuery(
    DateOnly? From = null, DateOnly? To = null, string? AccountKey = null, string? Category = null, string? Text = null,
    string? Direction = null, string? Status = TransactionStatuses.Posted)
{
    public const string Inflow = "inflow";
    public const string Outflow = "outflow";

    public void Validate()
    {
        if ((Category is not null && !SpendingCategories.All.Contains(Category, StringComparer.Ordinal)) ||
            Direction is not (null or Inflow or Outflow) || Status is not (null or TransactionStatuses.Posted or TransactionStatuses.Pending) ||
            (From is { } from && To is { } to && from > to) || Text?.Length > 128 || AccountKey?.Length > 64)
        {
            throw new ArgumentException("The ledger query is invalid.");
        }
    }

    public bool Matches(LedgerTransaction transaction) =>
        (From is null || transaction.PostedOn >= From) && (To is null || transaction.PostedOn <= To) &&
        (AccountKey is null || transaction.AccountKey == AccountKey) && (Category is null || transaction.Category == Category) &&
        (Status is null || transaction.Status == Status) &&
        (Direction is null || (Direction == Inflow ? transaction.Amount.Minor > 0 : transaction.Amount.Minor < 0)) &&
        (Text is null || transaction.Description.Contains(Text, StringComparison.OrdinalIgnoreCase) ||
         (transaction.Merchant?.Contains(Text, StringComparison.OrdinalIgnoreCase) ?? false));
}
