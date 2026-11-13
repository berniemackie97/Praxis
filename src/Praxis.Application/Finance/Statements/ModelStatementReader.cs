using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Praxis.Domain.Finance;

namespace Praxis.Application.Finance.Statements;

/// <summary>
/// Validates a model's statement transcription against the source text and arithmetic. Every
/// amount and balance must be printed in the statement, every date must be a real date in the
/// statement's period, and the rows must reconcile the balances exactly; otherwise nothing is
/// accepted.
/// </summary>
public static class ModelStatementReader
{
    public static FinancialReading? Read(string content, string statementText, FinancialInput input, string modelParser)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("isStatement", out JsonElement isStatement) &&
                isStatement.ValueKind == JsonValueKind.False)
            {
                return FinancialReading.NotAFinancialDocument;
            }

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("accountKind", out JsonElement kindElement) ||
                !root.TryGetProperty("transactions", out JsonElement rows) || rows.ValueKind != JsonValueKind.Array ||
                rows.GetArrayLength() > StatementExtractionPrompt.MaximumTransactions)
            {
                return null;
            }

            bool card = kindElement.GetString() == "card";
            string printedDigits = Digits(statementText);
            MonetaryAmount? opening = Balance(root, "openingBalance", card, printedDigits);
            MonetaryAmount? closing = Balance(root, "closingBalance", card, printedDigits);
            if (opening is null || closing is null)
            {
                return null;
            }

            string? lastFour = Text(root, "accountLastFour") is { Length: 4 } digits && digits.All(char.IsAsciiDigit)
                ? digits
                : AccountIdentity.FromFileName(input.FileName);
            (DateOnly? periodStart, DateOnly? periodEnd) = Period(statementText);
            FinancialAccount account = AccountIdentity.Account(lastFour, input.FallbackAccountKey,
                card ? AccountKinds.CreditCard : AccountKinds.Checking, null, "USD");
            List<ParsedTransaction> transactions = [];
            foreach (JsonElement row in rows.EnumerateArray())
            {
                if (row.ValueKind != JsonValueKind.Object ||
                    !DateOnly.TryParseExact(Text(row, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly posted) ||
                    (periodStart is { } start && posted < start.AddDays(-31)) || (periodEnd is { } end && posted > end.AddDays(7)) ||
                    Text(row, "description") is not { Length: > 0 } description ||
                    !MoneyText.TryParse(Text(row, "amount"), out decimal value, out _) ||
                    !Printed(value, printedDigits) ||
                    !MonetaryAmount.TryFromDecimal(value, "USD", out MonetaryAmount amount))
                {
                    return null;
                }

                transactions.Add(new ParsedTransaction(transactions.Count, account.AccountKey, 0, posted, amount,
                    description.Length <= FinanceLimits.MaximumDescriptionLength ? description : description[..FinanceLimits.MaximumDescriptionLength],
                    TransactionStatuses.Posted, null, null, null, null, null, $"model-row:{transactions.Count + 1}", TransactionOrigins.Model));
            }

            (string verification, _) = StatementBalanceProof.Verify(transactions.Select(row => row.Amount).ToArray(), opening, closing, []);
            if (verification != StatementVerifications.Balanced)
            {
                return null;
            }

            DateOnly[] dates = transactions.Select(row => row.PostedOn).ToArray();
            ParsedStatement statement = new(0, account.AccountKey, periodStart ?? (dates.Length == 0 ? null : dates.Min()),
                periodEnd ?? (dates.Length == 0 ? null : dates.Max()), opening, closing, StatementVerifications.Balanced, null);
            return FinancialReading.Of(new FinancialDocument(FinancialFormats.PdfStatement, modelParser, [account], [statement], transactions, []));
        }
    }

    /// <summary>The first "date – date" range printed in the statement, used only to bound transcribed dates.</summary>
    private static (DateOnly? Start, DateOnly? End) Period(string text)
    {
        Match match = PeriodPattern.Match(text);
        return match.Success &&
               DateText.TryParse(match.Groups["start"].Value, DateOrder.MonthFirst, null, out DateOnly start) &&
               DateText.TryParse(match.Groups["end"].Value, DateOrder.MonthFirst, null, out DateOnly end) && start <= end
            ? (start, end)
            : (null, null);
    }

    private static readonly Regex AmountPattern = new(MoneyText.StatementAmountPattern,
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    private static readonly Regex PeriodPattern = new(
        @"(?<start>\d{1,2}/\d{1,2}/\d{2,4}|\d{4}-\d{2}-\d{2}|[A-Z][a-z]{2,8}\.?\s+\d{1,2},?\s+\d{4})\s*(?:-|–|—|to|through|thru)\s*(?<end>\d{1,2}/\d{1,2}/\d{2,4}|\d{4}-\d{2}-\d{2}|[A-Z][a-z]{2,8}\.?\s+\d{1,2},?\s+\d{4})",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static MonetaryAmount? Balance(JsonElement root, string property, bool card, string printedDigits)
    {
        if (!MoneyText.TryParse(Text(root, property), out decimal value, out _) || !Printed(value, printedDigits) ||
            !MonetaryAmount.TryFromDecimal(value, "USD", out MonetaryAmount stated))
        {
            return null;
        }

        return card ? stated.Negate() : stated;
    }

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;

    /// <summary>True when the amount's digits (without grouping or sign) occur in the statement.</summary>
    private static bool Printed(decimal value, string printedDigits) =>
        printedDigits.Contains("|" + Math.Abs(value).ToString("0.00", CultureInfo.InvariantCulture) + "|", StringComparison.Ordinal);

    /// <summary>Every printed amount in the text, normalized to <c>|1234.56|</c> tokens for exact lookup.</summary>
    private static string Digits(string text)
    {
        StringBuilder tokens = new("|");
        foreach (Match match in AmountPattern.Matches(text))
        {
            if (MoneyText.TryParse(match.Value, out decimal value, out _))
            {
                tokens.Append(Math.Abs(value).ToString("0.00", CultureInfo.InvariantCulture)).Append('|');
            }
        }

        return tokens.ToString();
    }
}
