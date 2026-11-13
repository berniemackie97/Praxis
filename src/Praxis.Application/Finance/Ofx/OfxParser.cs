using System.Globalization;
using Praxis.Domain.Finance;

namespace Praxis.Application.Finance.Ofx;

/// <summary>
/// Bank and card statements from OFX 1.x/2.x, QFX and QBO downloads. Amounts keep OFX's
/// account-holder sign (debits negative), FITIDs become provider identities, and the ledger
/// balance becomes the statement's closing balance. OFX lists carry no opening balance, so
/// their completeness is recorded as a provider export rather than a balance proof.
/// </summary>
public sealed class OfxParser : IFinancialFormatParser
{
    public const string ParserVersion = "ofx-v1";

    public string Format => FinancialFormats.Ofx;

    public bool CanParse(string text)
    {
        ReadOnlySpan<char> head = text.AsSpan(0, Math.Min(text.Length, 4096));
        return head.Contains("OFXHEADER", StringComparison.OrdinalIgnoreCase) ||
               head.Contains("<OFX>", StringComparison.OrdinalIgnoreCase);
    }

    public FinancialParseOutcome Parse(string text, FinancialInput input)
    {
        OfxElement? ofx = OfxMarkupReader.Read(text);
        if (ofx is null)
        {
            return FinancialParseOutcome.Rejected("ofx_malformed");
        }

        string? institution = ofx.Descendants("FI").Select(fi => fi.Text("ORG")).FirstOrDefault(org => org is not null);
        List<FinancialAccount> accounts = [];
        List<ParsedStatement> statements = [];
        List<ParsedTransaction> transactions = [];
        List<string> issues = [];
        IEnumerable<(OfxElement Statement, bool Card)> statementElements =
            ofx.Descendants("STMTRS").Select(element => (element, false))
                .Concat(ofx.Descendants("CCSTMTRS").Select(element => (element, true)));
        foreach ((OfxElement statement, bool card) in statementElements)
        {
            if (statements.Count >= FinanceLimits.MaximumStatementsPerDocument)
            {
                return FinancialParseOutcome.Rejected("too_many_statements");
            }

            string currency = statement.Text("CURDEF")?.ToUpperInvariant() ?? "USD";
            if (!CurrencyExponents.IsValidCode(currency))
            {
                return FinancialParseOutcome.Rejected("unsupported_currency");
            }

            OfxElement? from = statement.Child(card ? "CCACCTFROM" : "BANKACCTFROM");
            string kind = card ? AccountKinds.CreditCard : BankKind(from?.Text("ACCTTYPE"));
            FinancialAccount account = AccountIdentity.Account(AccountIdentity.LastFour(from?.Text("ACCTID")),
                input.FallbackAccountKey, kind, institution, currency);
            if (accounts.All(existing => existing.AccountKey != account.AccountKey))
            {
                accounts.Add(account);
            }

            int ordinal = statements.Count;
            OfxElement? list = statement.Child("BANKTRANLIST");
            foreach (OfxElement row in list?.Children.Where(child => child.Name == "STMTTRN") ?? [])
            {
                if (transactions.Count >= FinanceLimits.MaximumTransactionsPerDocument)
                {
                    return FinancialParseOutcome.Rejected("too_many_transactions");
                }

                ParsedTransaction? parsed = ReadTransaction(row, transactions.Count, account, ordinal, currency);
                if (parsed is null)
                {
                    issues.Add("skipped_invalid_rows");
                    continue;
                }

                transactions.Add(parsed);
            }

            OfxElement? ledger = statement.Child("LEDGERBAL");
            MonetaryAmount? closing = ledger is null ? null : Amount(ledger.Text("BALAMT"), currency);
            statements.Add(new ParsedStatement(ordinal, account.AccountKey,
                Date(list?.Text("DTSTART")), Date(list?.Text("DTEND")) ?? Date(ledger?.Text("DTASOF")),
                null, closing, StatementVerifications.ProviderExport, null));
        }

        if (statements.Count == 0)
        {
            return FinancialParseOutcome.Rejected(ofx.Descendants("INVSTMTRS").Any() ? "investment_statement_unsupported" : "ofx_no_statement");
        }

        return FinancialParseOutcome.Recognized(new FinancialDocument(Format, ParserVersion, accounts, statements,
            transactions, issues.Distinct(StringComparer.Ordinal).ToArray()));
    }

    private static ParsedTransaction? ReadTransaction(OfxElement row, int ordinal, FinancialAccount account, int statement, string currency)
    {
        DateOnly? posted = Date(row.Text("DTPOSTED")) ?? Date(row.Text("DTUSER"));
        string rowCurrency = row.Child("CURRENCY")?.Text("CURSYM")?.ToUpperInvariant() ?? currency;
        MonetaryAmount? amount = Amount(row.Text("TRNAMT"), rowCurrency);
        if (posted is null || amount is null)
        {
            return null;
        }

        string? name = row.Text("NAME") ?? row.Child("PAYEE")?.Text("NAME");
        string? memo = row.Text("MEMO");
        string description = Describe(name, memo);
        if (description.Length == 0)
        {
            description = row.Text("TRNTYPE") ?? "Transaction";
        }

        return new ParsedTransaction(ordinal, account.AccountKey, statement, posted.Value, amount.Value,
            description, TransactionStatuses.Posted, row.Text("TRNTYPE")?.ToLowerInvariant(), Bounded(row.Text("FITID"), 255),
            Bounded(row.Text("CHECKNUM"), 32), null, null, $"stmttrn:{ordinal + 1}", TransactionOrigins.Parser);
    }

    private static string Describe(string? name, string? memo)
    {
        string first = name?.Trim() ?? "";
        string second = memo?.Trim() ?? "";
        string combined = second.Length == 0 || first.Contains(second, StringComparison.OrdinalIgnoreCase)
            ? first
            : first.Length == 0 ? second : $"{first} {second}";
        return combined.Length > FinanceLimits.MaximumDescriptionLength ? combined[..FinanceLimits.MaximumDescriptionLength] : combined;
    }

    private static string BankKind(string? accountType) => accountType?.ToUpperInvariant() switch
    {
        "CHECKING" => AccountKinds.Checking,
        "SAVINGS" or "MONEYMRKT" or "CD" => AccountKinds.Savings,
        "CREDITLINE" => AccountKinds.Loan,
        _ => AccountKinds.Unknown
    };

    internal static DateOnly? Date(string? value) =>
        value is { Length: >= 8 } && DateOnly.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
            ? date
            : null;

    private static MonetaryAmount? Amount(string? value, string currency)
    {
        if (value is null)
        {
            return null;
        }

        string normalized = value.Trim();
        if (normalized.Contains(',') && !normalized.Contains('.'))
        {
            normalized = normalized.Replace(',', '.');
        }

        return decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal parsed) &&
               MonetaryAmount.TryFromDecimal(parsed, currency, out MonetaryAmount amount)
            ? amount
            : null;
    }

    private static string? Bounded(string? value, int limit) =>
        value is null ? null : value.Length <= limit ? value : value[..limit];
}
