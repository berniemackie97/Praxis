namespace Praxis.Domain.Finance;

/// <summary>What Praxis understood from one financial source revision, including documents it could not prove.</summary>
public sealed record FinancialDocumentSummary(
    Guid SourceId, Guid ItemId, int Revision, string SourceName, string Outcome, string? Format, string? ReasonCode,
    int AccountCount, int StatementCount, int TransactionCount, int UnverifiedStatementCount,
    IReadOnlyList<string> Issues, string? ModelName, DateTimeOffset RecordedUtc);
