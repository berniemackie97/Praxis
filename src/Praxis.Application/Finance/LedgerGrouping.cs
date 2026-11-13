namespace Praxis.Application.Finance;

/// <summary>Keys an aggregation may group by. Grouping carries no product meaning; the caller interprets totals.</summary>
public static class LedgerGrouping
{
    public const string None = "none";
    public const string Month = "month";
    public const string Category = "category";
    public const string Merchant = "merchant";
    public const string Account = "account";

    public static readonly IReadOnlyList<string> All = [None, Month, Category, Merchant, Account];
}
