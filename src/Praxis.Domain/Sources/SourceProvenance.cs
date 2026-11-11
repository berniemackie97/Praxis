namespace Praxis.Domain.Sources;

public sealed record SourceProvenance(
    Guid SourceId, Guid ItemId, string SourceKind, string Locator, string Status,
    DateTimeOffset CreatedUtc, int Revision, DateTimeOffset ObservedUtc,
    string OriginalSha256, long OriginalBytes, string? ProcessorKey,
    string? ProcessingState, string? FailureCode, string? ExtractionOutcome,
    string? ExtractionReasonCode, string? CaptureRoute, string? CaptureAuthorityBasis,
    string? CaptureScope, DateTimeOffset? CaptureRecordedUtc, bool RetryEligible);
