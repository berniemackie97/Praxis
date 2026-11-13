using System.Globalization;
using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Finance;

namespace Praxis.Application.Desktop.Actions;

/// <summary>
/// Read-only, semantics-free financial tools for the agent and first-party clients: recognized
/// documents, accounts, filtered transactions and exact grouped sums. Nothing here moves money,
/// contacts a bank or decides what a budget is.
/// </summary>
public sealed class FinanceActions(FinancialLedger ledger) : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.Of("financeDocuments", fields => new FinanceDocumentsResponse(ledger.Documents(fields.IntegerOrDefault("limit", 100, 1, 1_000)))),
        DesktopAction.Of("financeAccounts", _ => new FinanceAccountsResponse(ledger.Accounts())),
        DesktopAction.Of("financeTransactions", fields => new FinanceTransactionsResponse(
            ledger.Transactions(Query(fields), fields.IntegerOrDefault("limit", 200, 1, 1_000)))),
        DesktopAction.Of("financeAggregate", fields => new FinanceAggregateResponse(
            ledger.Aggregate(Query(fields), fields.OptionalText("groupBy", 16, "The groupBy field is invalid.") ?? LedgerGrouping.None)))
    ];

    private static LedgerQuery Query(DesktopRequestFields fields) => new(
        Date(fields, "from"), Date(fields, "to"), fields.OptionalText("accountKey", 64, "The accountKey field is invalid."),
        fields.OptionalText("category", 32, "The category field is invalid."), fields.OptionalText("query", 128, "The query field is invalid."),
        fields.OptionalText("direction", 8, "The direction field is invalid."),
        fields.OptionalText("status", 8, "The status field is invalid.") is { } status ? (status == "any" ? null : status) : "posted");

    private static DateOnly? Date(DesktopRequestFields fields, string property) =>
        fields.OptionalText(property, 10, $"The {property} field must be a yyyy-MM-dd date.") is not { } text ? null
        : DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date) ? date
        : throw new ArgumentException($"The {property} field must be a valid date.");
}
