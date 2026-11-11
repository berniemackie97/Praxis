namespace Praxis.Domain.Sources;

public sealed record CurrentFileVersion(Guid ItemId, int Revision, string Digest, long Length, string Locator);
