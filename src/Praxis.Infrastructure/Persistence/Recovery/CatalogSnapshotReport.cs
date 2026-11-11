namespace Praxis.Infrastructure.Persistence.Recovery;

public sealed record CatalogSnapshotReport(int ObjectCount, long TotalBytes, string CatalogSha256);
