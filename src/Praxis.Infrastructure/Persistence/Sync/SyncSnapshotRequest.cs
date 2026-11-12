namespace Praxis.Infrastructure.Persistence.Sync;

public sealed record SyncSnapshotRequest(
    Guid ChallengeId, byte[] Signature, OpaqueSyncSnapshotPull Snapshot);
