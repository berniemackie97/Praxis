namespace Praxis.Domain.Originals;

public sealed record OriginalExport(Guid ItemId, int Revision, string Sha256, long Length);
