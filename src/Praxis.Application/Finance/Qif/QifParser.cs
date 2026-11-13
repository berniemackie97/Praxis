using Praxis.Domain.Finance;

namespace Praxis.Application.Finance.Qif;

/// <summary>
/// Quicken Interchange Format bank, cash, card and liability lists. QIF has no currency field or
/// transaction identity, so the currency defaults to USD and duplicates are reconciled by
/// fingerprint. Date field order is inferred from every date in the file.
/// </summary>
public sealed class QifParser : IFinancialFormatParser
{
    public const string ParserVersion = "qif-v1";

    public string Format => FinancialFormats.Qif;

    public bool CanParse(string text)
    {
        ReadOnlySpan<char> head = text.AsSpan(0, Math.Min(text.Length, 2048)).TrimStart();
        return head.StartsWith("!Type:", StringComparison.OrdinalIgnoreCase) ||
               head.StartsWith("!Account", StringComparison.OrdinalIgnoreCase) ||
               head.StartsWith("!Option", StringComparison.OrdinalIgnoreCase);
    }

    public FinancialParseOutcome Parse(string text, FinancialInput input)
    {
        List<QifRecord> records = [];
        string? section = null;
        string? accountName = null;
        string? accountType = null;
        bool inAccountBlock = false;
        Dictionary<char, string> fields = [];
        int recordLine = 0;
        int lineNumber = 0;
        foreach (string rawLine in text.Split('\n'))
        {
            lineNumber++;
            string line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '!')
            {
                string header = line.Trim();
                inAccountBlock = header.Equals("!Account", StringComparison.OrdinalIgnoreCase);
                if (header.StartsWith("!Type:", StringComparison.OrdinalIgnoreCase))
                {
                    section = header[6..].Trim();
                }

                continue;
            }

            if (line[0] == '^')
            {
                if (inAccountBlock)
                {
                    accountName = fields.GetValueOrDefault('N');
                    accountType = fields.GetValueOrDefault('T');
                }
                else if (fields.Count > 0 && IsTransactionSection(section))
                {
                    records.Add(new QifRecord(new Dictionary<char, string>(fields), recordLine, section!, accountName, accountType));
                    if (records.Count > FinanceLimits.MaximumTransactionsPerDocument)
                    {
                        return FinancialParseOutcome.Rejected("too_many_transactions");
                    }
                }

                fields.Clear();
                recordLine = 0;
                continue;
            }

            if (recordLine == 0)
            {
                recordLine = lineNumber;
            }

            char code = char.ToUpperInvariant(line[0]);
            string value = line[1..].Trim();
            if (!fields.TryAdd(code, value) && code is 'M' or 'A')
            {
                fields[code] = fields[code] + " " + value;
            }
        }

        if (records.Count == 0)
        {
            return FinancialParseOutcome.Rejected("qif_no_transactions");
        }

        DateOrderInference.Result? order = DateOrderInference.Infer(records.Select(record => record.Fields.GetValueOrDefault('D') ?? "").ToArray());
        if (order is null)
        {
            return FinancialParseOutcome.Rejected("unreadable_dates");
        }

        List<string> issues = order.Assumed ? ["date_order_assumed_month_first"] : [];
        Dictionary<string, FinancialAccount> accounts = new(StringComparer.Ordinal);
        Dictionary<string, int> statementOrdinals = new(StringComparer.Ordinal);
        List<ParsedTransaction> transactions = [];
        foreach (QifRecord record in records)
        {
            string kind = Kind(record.AccountType ?? record.Section);
            string? lastFour = AccountIdentity.LastFour(record.AccountName) ?? AccountIdentity.FromFileName(input.FileName);
            FinancialAccount account = AccountIdentity.Account(lastFour, input.FallbackAccountKey, kind, null, "USD",
                record.AccountName is { Length: > 0 } name ? name : null);
            if (accounts.TryAdd(account.AccountKey, account))
            {
                statementOrdinals[account.AccountKey] = statementOrdinals.Count;
            }

            string? amountText = record.Fields.GetValueOrDefault('T') ?? record.Fields.GetValueOrDefault('U');
            if (!DateText.TryParse(record.Fields.GetValueOrDefault('D'), order.Order, null, out DateOnly posted) ||
                !MoneyText.TryParse(amountText, out decimal value, out _) ||
                !MonetaryAmount.TryFromDecimal(value, "USD", out MonetaryAmount amount))
            {
                issues.Add("skipped_invalid_rows");
                continue;
            }

            string payee = record.Fields.GetValueOrDefault('P') ?? "";
            string memo = record.Fields.GetValueOrDefault('M') ?? "";
            string description = (payee.Length > 0 ? payee : memo).Trim();
            if (description.Length == 0)
            {
                description = "Transaction";
            }

            if (description.Length > FinanceLimits.MaximumDescriptionLength)
            {
                description = description[..FinanceLimits.MaximumDescriptionLength];
            }

            string? category = record.Fields.GetValueOrDefault('L') is { Length: > 0 } label && !label.StartsWith('[') ? label : null;
            string? transfer = record.Fields.GetValueOrDefault('L') is { Length: > 2 } bracket && bracket.StartsWith('[') ? "transfer" : null;
            transactions.Add(new ParsedTransaction(transactions.Count, account.AccountKey, statementOrdinals[account.AccountKey],
                posted, amount, description, TransactionStatuses.Posted, transfer, null,
                record.Fields.GetValueOrDefault('N') is { Length: > 0 and <= 32 } check ? check : null,
                category, null, $"line:{record.Line}", TransactionOrigins.Parser));
        }

        ParsedStatement[] statements = accounts.Values.Select(account =>
        {
            DateOnly[] dates = transactions.Where(row => row.AccountKey == account.AccountKey).Select(row => row.PostedOn).ToArray();
            return new ParsedStatement(statementOrdinals[account.AccountKey], account.AccountKey,
                dates.Length == 0 ? null : dates.Min(), dates.Length == 0 ? null : dates.Max(),
                null, null, StatementVerifications.ProviderExport, null);
        }).ToArray();
        return FinancialParseOutcome.Recognized(new FinancialDocument(Format, ParserVersion, accounts.Values.ToArray(),
            statements, transactions, issues.Distinct(StringComparer.Ordinal).ToArray()));
    }

    private static bool IsTransactionSection(string? section) =>
        section is not null && !section.StartsWith("Invst", StringComparison.OrdinalIgnoreCase)
        && !section.Equals("Cat", StringComparison.OrdinalIgnoreCase) && !section.Equals("Class", StringComparison.OrdinalIgnoreCase)
        && !section.Equals("Memorized", StringComparison.OrdinalIgnoreCase);

    private static string Kind(string type) => type.Trim().ToUpperInvariant() switch
    {
        "BANK" => AccountKinds.Checking,
        "CCARD" or "CREDIT CARD" => AccountKinds.CreditCard,
        "OTH L" => AccountKinds.Loan,
        _ => AccountKinds.Unknown
    };

    private sealed record QifRecord(Dictionary<char, string> Fields, int Line, string Section, string? AccountName, string? AccountType);
}
