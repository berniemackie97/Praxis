using Praxis.Domain.Finance;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>Exact grouped sums over reconciled transactions.</summary>
public sealed record FinanceAggregateResponse(IReadOnlyList<LedgerAggregate> Groups);
