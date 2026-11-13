namespace Praxis.Application.Finance;

/// <summary>
/// The context a parser may use besides content: the source's file name (which often carries
/// the last account digits) and the account key to use when a document identifies no account.
/// </summary>
public sealed record FinancialInput(string FileName, string FallbackAccountKey);
