using Praxis.Domain.Security;

namespace Praxis.Infrastructure.Persistence.Security.Registry;

/// <summary>
/// An authenticated request result, not a durable session or bearer token. Before every
/// data operation, callers must additionally check IsCurrent against the registry.
/// </summary>
public sealed class VerifiedDeviceProof
{
    internal VerifiedDeviceProof(Guid installationId, Guid principalId, Guid deviceId,
        Guid spaceId, Guid? sourceId, SecurityPermission permission, long grantVersion, DateTimeOffset expiresUtc)
    {
        InstallationId = installationId;
        PrincipalId = principalId;
        DeviceId = deviceId;
        SpaceId = spaceId;
        SourceId = sourceId;
        Permission = permission;
        GrantVersion = grantVersion;
        ExpiresUtc = expiresUtc;
    }

    internal Guid InstallationId { get; }
    public Guid PrincipalId { get; }
    public Guid DeviceId { get; }
    public Guid SpaceId { get; }
    public Guid? SourceId { get; }
    public SecurityPermission Permission { get; }
    internal long GrantVersion { get; }
    public DateTimeOffset ExpiresUtc { get; }
}
