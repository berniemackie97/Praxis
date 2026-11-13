using Praxis.Domain.Finance;

namespace Praxis.Application.Finance.Delimited;

/// <summary>
/// Applies a model's mapping to every row with exact parsing, then verifies it. A mapping is
/// rejected when fewer than 90% of data rows parse or when a mapped running balance does not
/// follow from the signed amounts (in either row order). A running-balance proof upgrades the
/// export to <see cref="StatementVerifications.RunningBalance"/>; the
/// (the requireProof argument, used for the low-trust local tier) rejects any mapping that cannot be proven this way.
/// </summary>
public static class DelimitedMappingApplier
{
    public const string ParserVersion = DelimitedMappingPrompt.Version;

    public static FinancialDocument? Apply(DelimitedMapping mapping, IReadOnlyList<DelimitedRow> rows, FinancialInput input, bool requireProof)
    {
        if (!mapping.IsFinancial)
        {
            return null;
        }

        DelimitedRow[] data = rows.Skip(mapping.HeaderRow + 1).ToArray();
        List<(DelimitedRow Row, decimal Amount, decimal? Balance, string Account)> parsed = [];
        int skipped = 0;
        foreach (DelimitedRow row in data)
        {
            if (!DateText.TryParse(Field(row, mapping.DateColumn), mapping.DateOrder, null, out _) || Amount(row, mapping) is not { } amount)
            {
                skipped++;
                continue;
            }

            decimal? balance = mapping.BalanceColumn is { } column && MoneyText.TryParse(Field(row, column), out decimal stated, out _) ? stated : null;
            parsed.Add((row, mapping.InvertAmounts ? -amount : amount, balance, Field(row, mapping.AccountColumn)));
        }

        if (parsed.Count == 0 || skipped * 10 > data.Length)
        {
            return null;
        }

        bool proven = mapping.BalanceColumn is not null && RunningBalanceHolds(parsed.Select(item => (item.Amount, item.Balance)).ToArray());
        if ((mapping.BalanceColumn is not null && !proven) || (requireProof && !proven))
        {
            return null;
        }

        Dictionary<string, FinancialAccount> accounts = new(StringComparer.Ordinal);
        Dictionary<string, int> statements = new(StringComparer.Ordinal);
        List<ParsedTransaction> transactions = [];
        foreach ((DelimitedRow row, decimal value, decimal? balance, string accountText) in parsed)
        {
            string? lastFour = AccountIdentity.LastFour(accountText) ?? mapping.AccountLastFour ?? AccountIdentity.FromFileName(input.FileName);
            FinancialAccount account = AccountIdentity.Account(lastFour, input.FallbackAccountKey, mapping.AccountKind, null, mapping.Currency);
            if (accounts.TryAdd(account.AccountKey, account))
            {
                statements[account.AccountKey] = statements.Count;
            }

            DateText.TryParse(Field(row, mapping.DateColumn), mapping.DateOrder, null, out DateOnly posted);
            if (!MonetaryAmount.TryFromDecimal(value, mapping.Currency, out MonetaryAmount amount))
            {
                return null;
            }

            MonetaryAmount? after = balance is { } stated && MonetaryAmount.TryFromDecimal(stated, mapping.Currency, out MonetaryAmount money) ? money : null;
            string description = string.Join(" ", mapping.DescriptionColumns.Select(column => Field(row, column)).Where(text => text.Length > 0));
            description = description.Length == 0 ? "Transaction" : description.Length > FinanceLimits.MaximumDescriptionLength
                ? description[..FinanceLimits.MaximumDescriptionLength] : description;
            bool pending = Field(row, mapping.StatusColumn).Contains("pend", StringComparison.OrdinalIgnoreCase);
            transactions.Add(new ParsedTransaction(transactions.Count, account.AccountKey, statements[account.AccountKey], posted, amount, description,
                pending ? TransactionStatuses.Pending : TransactionStatuses.Posted, null, null, null, null, after, $"row:{row.Line}", TransactionOrigins.Parser));
        }

        string verification = proven ? StatementVerifications.RunningBalance : StatementVerifications.ProviderExport;
        ParsedStatement[] periods = accounts.Values.Select(account =>
        {
            DateOnly[] dates = transactions.Where(row => row.AccountKey == account.AccountKey).Select(row => row.PostedOn).ToArray();
            return new ParsedStatement(statements[account.AccountKey], account.AccountKey, dates.Min(), dates.Max(), null, null, verification, null);
        }).ToArray();
        return new FinancialDocument(FinancialFormats.Delimited, ParserVersion, accounts.Values.ToArray(), periods, transactions,
            skipped > 0 ? ["skipped_invalid_rows"] : []);
    }

    private static decimal? Amount(DelimitedRow row, DelimitedMapping mapping)
    {
        if (mapping.AmountColumn is { } column)
        {
            if (!MoneyText.TryParse(Field(row, column), out decimal value, out MoneyMarker marker))
            {
                return null;
            }

            return marker switch
            {
                MoneyMarker.Credit => Math.Abs(value),
                MoneyMarker.Debit => -Math.Abs(value),
                _ => value
            };
        }

        bool debit = MoneyText.TryParse(Field(row, mapping.DebitColumn), out decimal debitValue, out _);
        bool credit = MoneyText.TryParse(Field(row, mapping.CreditColumn), out decimal creditValue, out _);
        return debit || credit ? (credit ? Math.Abs(creditValue) : 0) - (debit ? Math.Abs(debitValue) : 0) : null;
    }

    private static bool RunningBalanceHolds(IReadOnlyList<(decimal Amount, decimal? Balance)> rows)
    {
        if (rows.Count < 2 || rows.Any(row => row.Balance is null))
        {
            return false;
        }

        bool ascending = true;
        bool descending = true;
        for (int index = 1; index < rows.Count; index++)
        {
            ascending &= rows[index].Balance == rows[index - 1].Balance + rows[index].Amount;
            descending &= rows[index - 1].Balance == rows[index].Balance + rows[index - 1].Amount;
        }

        return ascending || descending;
    }

    private static string Field(DelimitedRow row, int? column) =>
        column is { } index && index < row.Fields.Count ? row.Fields[index].Trim() : "";
}
