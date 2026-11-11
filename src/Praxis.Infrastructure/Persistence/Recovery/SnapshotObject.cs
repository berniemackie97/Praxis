namespace Praxis.Infrastructure.Persistence.Recovery;

internal sealed record SnapshotObject(string Digest, long Length);
