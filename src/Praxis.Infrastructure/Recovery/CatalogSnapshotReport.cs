namespace Praxis.Infrastructure.Recovery;

public sealed record CatalogSnapshotReport(int ObjectCount, long TotalBytes, string CatalogSha256);
