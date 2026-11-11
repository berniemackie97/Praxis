namespace Praxis.Domain.Sources;

public sealed record SourceProcessingState(
    string ProcessorKey, string State, int Attempt, int TotalAttempts,
    string? FailureCode, DateTimeOffset UpdatedUtc, bool RetryEligible);
