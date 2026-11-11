namespace Praxis.Domain.Processing;

public sealed record ProcessingLease(Guid ItemId, int Revision, string ProcessorKey, Guid LeaseKey, DateTimeOffset LeaseUntil);
