using Praxis.Domain.Security;

namespace Praxis.Infrastructure.Persistence.Security.Registry;

/// <summary>Immutable identity and resource selection validated by the security registry.</summary>
public sealed class AuthorizedDataScope
{
    internal AuthorizedDataScope(Guid principalId, Guid deviceId, Guid spaceId,
        Guid sourceId, SecurityPermission permission, long contentEpoch)
    {
        PrincipalId = principalId;
        DeviceId = deviceId;
        SpaceId = spaceId;
        SourceId = sourceId;
        Permission = permission;
        ContentEpoch = contentEpoch;
    }

    public Guid PrincipalId { get; }
    public Guid DeviceId { get; }
    public Guid SpaceId { get; }
    public Guid SourceId { get; }
    public SecurityPermission Permission { get; }
    public long ContentEpoch { get; }
}
