namespace Praxis.Domain.Finance;

/// <summary>
/// How a statement's rows were proven complete. Only verified statements contribute to ledger
/// totals; an export without balances is accepted as a provider record, not a proof.
/// </summary>
public static class StatementVerifications
{
    /// <summary>Opening balance plus the signed rows equals the closing balance exactly.</summary>
    public const string Balanced = "balanced";

    /// <summary>Every row's running balance follows from the previous row and its amount.</summary>
    public const string RunningBalance = "running_balance";

    /// <summary>A structured provider export (OFX, QIF, delimited) without balances to prove.</summary>
    public const string ProviderExport = "provider_export";

    /// <summary>The rows could not be proven complete; they are retained for review only.</summary>
    public const string Unverified = "unverified";

    public static readonly IReadOnlyList<string> All = [Balanced, RunningBalance, ProviderExport, Unverified];

    public static bool Counts(string verification) => verification != Unverified;
}
