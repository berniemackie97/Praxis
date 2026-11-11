using Praxis.Domain.Security;

namespace Praxis.Infrastructure.Persistence.Sync;

public sealed record SyncChallengeRequest(
    Guid DeviceId, Guid SpaceId, SecurityPermission Permission);
