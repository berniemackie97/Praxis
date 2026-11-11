namespace Praxis.Domain.Originals;

public sealed record CapturedOriginal(Guid SourceId, Guid ItemId, int Revision, string Sha256, long Length);
