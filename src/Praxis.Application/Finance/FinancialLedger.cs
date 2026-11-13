using Praxis.Application.Finance.Reconciliation;
using Praxis.Domain.Finance;

namespace Praxis.Application.Finance;

/// <summary>
/// Exact, semantics-free access to the owner's financial records for the agent: reconciled
/// transactions with their supporting rows, grouped integer sums, accounts with their latest
/// proven balance, and recognition outcomes. What counts as spending, a budget or a saving is
/// decided by the agent from these facts, not here.
/// </summary>
public sealed class FinancialLedger(IFinancialLedgerRepository repository)
{
    public IReadOnlyList<FinancialDocumentSummary> Documents(int limit) =>
        repository.ReadDocuments(Math.Clamp(limit, 1, FinanceLimits.MaximumQueryResults));

    public IReadOnlyList<LedgerTransaction> Transactions(LedgerQuery query, int limit = 200)
    {
        query.Validate();
        return Build().Where(query.Matches)
            .OrderByDescending(transaction => transaction.PostedOn).ThenBy(transaction => transaction.TransactionId, StringComparer.Ordinal)
            .Take(Math.Clamp(limit, 1, FinanceLimits.MaximumQueryResults))
            .ToArray();
    }

    /// <summary>Exact sums by group and currency. Amounts are never mixed across currencies.</summary>
    public IReadOnlyList<LedgerAggregate> Aggregate(LedgerQuery query, string groupBy)
    {
        query.Validate();
        if (!LedgerGrouping.All.Contains(groupBy, StringComparer.Ordinal))
        {
            throw new ArgumentException("The grouping is not supported.", nameof(groupBy));
        }

        return Build().Where(query.Matches)
            .GroupBy(transaction => (Key: Key(transaction, groupBy), transaction.Amount.Currency))
            .Select(group => new LedgerAggregate(group.Key.Key, group.Key.Currency,
                group.Aggregate(0L, (sum, item) => checked(sum + item.Amount.Minor)),
                group.Where(item => item.Amount.Minor > 0).Aggregate(0L, (sum, item) => checked(sum + item.Amount.Minor)),
                group.Where(item => item.Amount.Minor < 0).Aggregate(0L, (sum, item) => checked(sum - item.Amount.Minor)),
                group.Count()))
            .OrderBy(aggregate => aggregate.Key, StringComparer.Ordinal).ThenBy(aggregate => aggregate.Currency, StringComparer.Ordinal)
            .Take(FinanceLimits.MaximumQueryResults)
            .ToArray();
    }

    public IReadOnlyList<LedgerAccount> Accounts()
    {
        IReadOnlyList<LedgerTransaction> transactions = Build();
        IReadOnlyList<StoredStatement> statements = repository.ReadCountedStatements();
        return MergedAccounts().Values.Select(account =>
        {
            LedgerTransaction[] own = transactions.Where(transaction => transaction.AccountKey == account.AccountKey).ToArray();
            StoredStatement? latest = statements
                .Where(statement => statement.Account.AccountKey == account.AccountKey && statement.Statement.ClosingBalance is not null)
                .OrderByDescending(statement => statement.Statement.PeriodEnd ?? DateOnly.MinValue)
                .ThenByDescending(statement => statement.Evidence.Revision)
                .FirstOrDefault();
            return new LedgerAccount(account.AccountKey, account.Kind, account.Mask, account.Institution, account.Currency, account.DisplayName,
                own.Length, own.Length == 0 ? null : own.Min(transaction => transaction.PostedOn),
                own.Length == 0 ? null : own.Max(transaction => transaction.PostedOn),
                latest?.Statement.ClosingBalance, latest?.Statement.PeriodEnd, latest?.Evidence);
        }).OrderBy(account => account.DisplayName, StringComparer.Ordinal).ToArray();
    }

    private IReadOnlyList<LedgerTransaction> Build() =>
        LedgerReconciler.Reconcile(repository.ReadCountedTransactions()).Select(group =>
        {
            StoredTransaction representative = group[0];
            TransactionAnnotation? annotation = group.Select(row => row.Annotation).FirstOrDefault(item => item is not null);
            string status = group.Any(row => row.Row.Status == TransactionStatuses.Posted) ? TransactionStatuses.Posted : TransactionStatuses.Pending;
            return new LedgerTransaction(LedgerReconciler.TransactionId(group), representative.Row.AccountKey, representative.Row.PostedOn,
                representative.Row.Amount, representative.Row.Description, annotation?.Merchant,
                annotation?.Category ?? SpendingCategories.Uncategorized, annotation?.Tier, status, group.Select(row => row.Evidence).ToArray());
        }).ToArray();

    private static string Key(LedgerTransaction transaction, string groupBy) => groupBy switch
    {
        LedgerGrouping.Month => transaction.PostedOn.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
        LedgerGrouping.Category => transaction.Category,
        LedgerGrouping.Merchant => transaction.Merchant ?? transaction.Description,
        LedgerGrouping.Account => transaction.AccountKey,
        _ => "all"
    };

    /// <summary>One account per key, preferring the most specific kind and any reported institution.</summary>
    private IReadOnlyDictionary<string, FinancialAccount> MergedAccounts() =>
        repository.ReadAccounts().GroupBy(account => account.AccountKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group
                .OrderBy(account => account.Kind == AccountKinds.Unknown ? 1 : 0)
                .First() with
            {
                Institution = group.Select(account => account.Institution).FirstOrDefault(institution => institution is not null)
            }, StringComparer.Ordinal);
}
