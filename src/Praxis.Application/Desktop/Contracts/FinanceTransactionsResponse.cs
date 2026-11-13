using Praxis.Domain.Finance;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>Reconciled transactions with the document rows that support each one.</summary>
public sealed record FinanceTransactionsResponse(IReadOnlyList<LedgerTransaction> Transactions);
