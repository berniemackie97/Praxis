namespace Praxis.Infrastructure.Persistence.Security.Registry;

internal sealed record SecuritySnapshotManifest(int FormatVersion, int SchemaVersion, Guid InstallationId,
    long DatabaseLength, string DatabaseSha256);
