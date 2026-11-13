using Praxis.Domain.Finance;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>Owned accounts with their latest proven balances.</summary>
public sealed record FinanceAccountsResponse(IReadOnlyList<LedgerAccount> Accounts);
