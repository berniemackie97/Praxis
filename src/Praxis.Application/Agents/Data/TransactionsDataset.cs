using System.Globalization;
using Praxis.Application.Finance;
using Praxis.Domain.Agents;
using Praxis.Domain.Finance;

namespace Praxis.Application.Agents.Data;

/// <summary>Reconciled bank and card transactions, one row per real-world transaction, with supporting document rows as evidence.</summary>
public sealed class TransactionsDataset(FinancialLedger ledger) : IDataset
{
    public string Name => "transactions";

    public string Description => "Reconciled bank and card transactions (overlapping exports merged). amount is signed minor units: negative = money out.";

    public IReadOnlyList<DataField> Fields { get; } =
    [
        new("id", DataField.Text, "Stable transaction id", false),
        new("date", DataField.Date, "Posted date", true),
        new("month", DataField.Text, "yyyy-MM of date", true),
        new("account", DataField.Text, "Account key (see accounts dataset)", true),
        new("amount", DataField.Money, "Signed amount in minor units", false),
        new("currency", DataField.Text, "ISO currency", true),
        new("direction", DataField.Text, "inflow or outflow", true),
        new("category", DataField.Text, "Model-annotated category", true),
        new("merchant", DataField.Text, "Model-annotated merchant", true),
        new("description", DataField.Text, "Bank description as printed", false),
        new("status", DataField.Text, "posted or pending", true)
    ];

    public DatasetSnapshot Snapshot(AgentToolContext context)
    {
        IReadOnlyList<LedgerTransaction> visible = ledger.AllTransactions(context.MayDisclose);
        int withheld = context.BackendIsLocal ? 0 : Math.Max(0, ledger.AllTransactions().Count - visible.Count);
        DataRow[] rows = visible.Select(transaction => new DataRow(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = transaction.TransactionId,
            ["date"] = transaction.PostedOn,
            ["month"] = transaction.PostedOn.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            ["account"] = transaction.AccountKey,
            ["amount"] = transaction.Amount.Minor,
            ["currency"] = transaction.Amount.Currency,
            ["direction"] = transaction.Amount.Minor > 0 ? "inflow" : "outflow",
            ["category"] = transaction.Category,
            ["merchant"] = transaction.Merchant,
            ["description"] = transaction.Description,
            ["status"] = transaction.Status
        }, transaction.Evidence.Select(item => new AgentCitation(item.SourceId, item.ItemId, item.Revision, item.Locator, item.SourceName)).ToArray())).ToArray();
        return new DatasetSnapshot(rows, withheld, Gaps(ledger, visible, context));
    }

    /// <summary>Months with no rows between an account's first and last activity, and documents not yet usable.</summary>
    private static IReadOnlyList<string> Gaps(FinancialLedger ledger, IReadOnlyList<LedgerTransaction> rows, AgentToolContext context)
    {
        List<string> gaps = [];
        foreach (IGrouping<string, LedgerTransaction> account in rows.GroupBy(row => row.AccountKey, StringComparer.Ordinal))
        {
            DateOnly first = account.Min(row => row.PostedOn);
            DateOnly last = account.Max(row => row.PostedOn);
            HashSet<string> present = account.Select(row => row.PostedOn.ToString("yyyy-MM", CultureInfo.InvariantCulture)).ToHashSet(StringComparer.Ordinal);
            for (DateOnly month = new(first.Year, first.Month, 1); month <= last; month = month.AddMonths(1))
            {
                string key = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                if (!present.Contains(key))
                {
                    gaps.Add($"{account.Key}: no transactions recorded for {key}");
                }
            }
        }

        foreach (IGrouping<string, FinancialDocumentSummary> outcome in ledger.Documents(500, context.MayDisclose)
                     .Where(document => document.Outcome != "completed").GroupBy(document => document.ReasonCode ?? "unknown"))
        {
            gaps.Add($"{outcome.Count()} financial file(s) not used: {outcome.Key}");
        }

        return gaps.Take(40).ToArray();
    }
}
