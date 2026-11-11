using Praxis.Domain.Security;

namespace Praxis.Infrastructure.Persistence.Sync;

public sealed record SyncChallengeRequest(
    Guid DeviceId, Guid SpaceId, SecurityPermission Permission);

public sealed record SyncSubmitRequest(
    Guid ChallengeId, byte[] Signature, OpaqueSyncMutation Mutation);

public sealed record SyncPullRequest(
    Guid ChallengeId, byte[] Signature, OpaqueSyncPull Pull);
