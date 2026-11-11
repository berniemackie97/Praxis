namespace Praxis.Domain.Sources;

public sealed record SourceMovement(Guid SourceId, string PreviousPath, string CurrentPath, string Sha256, DateTimeOffset RecordedUtc);
