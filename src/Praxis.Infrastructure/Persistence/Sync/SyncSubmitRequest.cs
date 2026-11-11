namespace Praxis.Infrastructure.Persistence.Sync;

public sealed record SyncSubmitRequest(
    Guid ChallengeId, byte[] Signature, OpaqueSyncMutation Mutation);
