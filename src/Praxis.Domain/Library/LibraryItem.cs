namespace Praxis.Domain.Library;

public sealed record LibraryItem(Guid SourceId, Guid ItemId, string SourceKind, string SourceLocator, string SourceStatus, int Revision, string Sha256, long OriginalBytes, string? ProcessingState, string? ExtractionOutcome);
