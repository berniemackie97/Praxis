namespace Praxis.Domain.Sources;

public sealed record SourceFileVersion(Guid SourceId, Guid ItemId, int Revision, string Sha256, string Status);
