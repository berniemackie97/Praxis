namespace Praxis.Application.Finance.Delimited;

/// <summary>
/// A model's reading of a delimited export: which row is the header, what each column means,
/// the date field order and the sign convention. Code applies it to every row and verifies it.
/// Column indexes are zero-based.
/// </summary>
public sealed record DelimitedMapping(
    bool IsFinancial, int HeaderRow, int DateColumn, DateOrder DateOrder, IReadOnlyList<int> DescriptionColumns,
    int? AmountColumn, int? DebitColumn, int? CreditColumn, bool InvertAmounts, int? BalanceColumn, int? StatusColumn,
    int? AccountColumn, string Currency, string? AccountLastFour, string AccountKind);
