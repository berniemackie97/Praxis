namespace Praxis.Infrastructure.Persistence.Sync;

public sealed record SyncRotationCommitRequest(
    Guid ChallengeId, byte[] Signature, OpaqueSyncRotationCommit Commit);
