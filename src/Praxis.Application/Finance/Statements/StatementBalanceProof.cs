using Praxis.Domain.Finance;

namespace Praxis.Application.Finance.Statements;

/// <summary>
/// Proves a statement's rows complete with exact arithmetic. Balances are in owner-equity sign
/// (amounts owed on cards and loans are negative), so one equation serves every account kind:
/// closing = opening + Σ signed rows. A running-balance column proves each row individually.
/// </summary>
public static class StatementBalanceProof
{
    public static (string Verification, string? Note) Verify(
        IReadOnlyList<MonetaryAmount> amounts, MonetaryAmount? opening, MonetaryAmount? closing,
        IReadOnlyList<MonetaryAmount?> runningBalances)
    {
        if (opening is { } start && closing is { } end)
        {
            if (start.Currency != end.Currency || amounts.Any(amount => amount.Currency != start.Currency))
            {
                return (StatementVerifications.Unverified, "mixed_currency");
            }

            long total = start.Minor;
            foreach (MonetaryAmount amount in amounts)
            {
                total = checked(total + amount.Minor);
            }

            return total == end.Minor
                ? (StatementVerifications.Balanced, null)
                : (StatementVerifications.Unverified, "balance_mismatch");
        }

        return RunningBalancesHold(amounts, opening, runningBalances)
            ? (StatementVerifications.RunningBalance, null)
            : (StatementVerifications.Unverified, "balances_not_found");
    }

    private static bool RunningBalancesHold(IReadOnlyList<MonetaryAmount> amounts, MonetaryAmount? opening, IReadOnlyList<MonetaryAmount?> balances)
    {
        if (amounts.Count < 2 || balances.Count != amounts.Count || balances.Any(balance => balance is null))
        {
            return false;
        }

        long? previous = opening?.Minor;
        for (int index = 0; index < amounts.Count; index++)
        {
            long current = balances[index]!.Value.Minor;
            if (previous is { } prior && prior + amounts[index].Minor != current)
            {
                return false;
            }

            previous = current;
        }

        return true;
    }
}
