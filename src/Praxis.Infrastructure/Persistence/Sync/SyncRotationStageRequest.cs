namespace Praxis.Infrastructure.Persistence.Sync;

public sealed record SyncRotationStageRequest(
    Guid ChallengeId, byte[] Signature, OpaqueSyncRotationStage Stage);
