namespace Praxis.Infrastructure.Persistence.Sync;

public sealed record SyncPullRequest(
    Guid ChallengeId, byte[] Signature, OpaqueSyncPull Pull);
