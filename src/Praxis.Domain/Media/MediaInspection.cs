namespace Praxis.Domain.Media;

public sealed record MediaInspection(Guid SourceId, Guid ItemId, int Revision, string SourceSha256, long OriginalBytes, string SourceExtension, string Outcome, string? ContainerFamily, long? DurationMilliseconds, string? ReasonCode, string Producer);
