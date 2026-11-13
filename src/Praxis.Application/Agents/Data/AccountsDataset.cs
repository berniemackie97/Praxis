using Praxis.Application.Finance;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Data;

/// <summary>Owned accounts with masked numbers, coverage and latest proven balance.</summary>
public sealed class AccountsDataset(FinancialLedger ledger) : IDataset
{
    public string Name => "accounts";

    public string Description => "Owned bank/card/loan accounts. balance is the latest proven statement balance in minor units (debts negative).";

    public IReadOnlyList<DataField> Fields { get; } =
    [
        new("account", DataField.Text, "Account key", true),
        new("kind", DataField.Text, "checking, savings, credit_card, loan, investment, unknown", true),
        new("mask", DataField.Text, "Last digits", false),
        new("institution", DataField.Text, "Institution if known", true),
        new("currency", DataField.Text, "ISO currency", true),
        new("transactions", DataField.Integer, "Reconciled transaction count", false),
        new("firstDate", DataField.Date, "Earliest transaction", false),
        new("lastDate", DataField.Date, "Latest transaction", false),
        new("balance", DataField.Money, "Latest proven balance", false),
        new("balanceDate", DataField.Date, "Statement date of balance", false)
    ];

    public DatasetSnapshot Snapshot(AgentToolContext context) => new(ledger.Accounts(context.MayDisclose).Select(account => new DataRow(
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["account"] = account.AccountKey,
            ["kind"] = account.Kind,
            ["mask"] = account.Mask,
            ["institution"] = account.Institution,
            ["currency"] = account.Currency,
            ["transactions"] = account.TransactionCount,
            ["firstDate"] = account.FirstPostedOn,
            ["lastDate"] = account.LastPostedOn,
            ["balance"] = account.LatestBalance?.Minor,
            ["balanceDate"] = account.LatestBalanceOn
        },
        account.LatestBalanceEvidence is { } evidence
            ? [new AgentCitation(evidence.SourceId, evidence.ItemId, evidence.Revision, evidence.Locator, evidence.SourceName)]
            : [])).ToArray(), 0, []);
}
