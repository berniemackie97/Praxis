namespace Praxis.Domain.Finance;

/// <summary>Resource bounds for financial documents and ledger queries.</summary>
public static class FinanceLimits
{
    public const int MaximumDocumentBytes = 32 * 1024 * 1024;
    public const int MaximumTransactionsPerDocument = 100_000;
    public const int MaximumStatementsPerDocument = 64;
    public const int MaximumLedgerTransactions = 500_000;
    public const int MaximumDescriptionLength = 512;
    public const int MaximumLocatorLength = 128;
    public const int MaximumPdfPages = 200;
    public const int MaximumQueryResults = 1_000;
}
