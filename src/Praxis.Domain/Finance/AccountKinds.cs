namespace Praxis.Domain.Finance;

/// <summary>Account kinds. Card and loan balances are amounts owed; deposit balances are amounts held.</summary>
public static class AccountKinds
{
    public const string Checking = "checking";
    public const string Savings = "savings";
    public const string CreditCard = "credit_card";
    public const string Loan = "loan";
    public const string Investment = "investment";
    public const string Unknown = "unknown";

    public static readonly IReadOnlyList<string> All = [Checking, Savings, CreditCard, Loan, Investment, Unknown];

    /// <summary>True when the statement balance is an amount owed rather than held.</summary>
    public static bool IsLiability(string kind) => kind is CreditCard or Loan;
}
