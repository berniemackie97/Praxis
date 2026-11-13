namespace Praxis.Domain.Finance;

/// <summary>The typed content recognized in one source revision.</summary>
public sealed record FinancialDocument(
    string Format, string Parser, IReadOnlyList<FinancialAccount> Accounts, IReadOnlyList<ParsedStatement> Statements,
    IReadOnlyList<ParsedTransaction> Transactions, IReadOnlyList<string> Issues);
