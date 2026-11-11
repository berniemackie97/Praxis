using Praxis.Domain.Security;

namespace Praxis.Infrastructure.Persistence.Security.Registry;

public sealed class AuthorizedSpaceScope
{
    internal AuthorizedSpaceScope(Guid principalId, Guid deviceId, Guid spaceId,
        SecurityPermission permission, long contentEpoch)
    {
        PrincipalId = principalId;
        DeviceId = deviceId;
        SpaceId = spaceId;
        Permission = permission;
        ContentEpoch = contentEpoch;
    }

    public Guid PrincipalId { get; }
    public Guid DeviceId { get; }
    public Guid SpaceId { get; }
    public SecurityPermission Permission { get; }
    public long ContentEpoch { get; }
}
