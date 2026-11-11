namespace Praxis.Infrastructure.Persistence.Security.Registry;

public sealed record SecuritySnapshotReport(Guid InstallationId, long DatabaseBytes, string DatabaseSha256);
