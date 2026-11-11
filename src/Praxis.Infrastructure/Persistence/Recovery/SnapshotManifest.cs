namespace Praxis.Infrastructure.Persistence.Recovery;

internal sealed record SnapshotManifest(int FormatVersion, int CatalogVersion, string CatalogSha256, long CatalogLength, List<SnapshotObject> Objects);
