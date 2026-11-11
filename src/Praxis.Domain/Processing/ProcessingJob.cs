namespace Praxis.Domain.Processing;

public sealed record ProcessingJob(Guid ItemId, int Revision, string ProcessorKey, string State, int Attempt, int TotalAttempts, DateTimeOffset? LeaseUntil, string? FailureCode);
