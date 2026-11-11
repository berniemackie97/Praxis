namespace Praxis.Domain.Security;

/// <summary>A signed approval. A client must verify this against an independently pinned owner key.</summary>
public sealed record ApprovedDeviceIdentity(
    Guid InstallationId, Guid ChallengeId, Guid DeviceId, Guid SpaceId, Guid PrincipalId,
    Guid ApproverDeviceId, byte[] SigningPublicKey, byte[] WrappingPublicKey,
    byte[] WrappingSignature, byte[] Nonce, DateTimeOffset ExpiresUtc,
    byte[] OwnerSignature, byte[] ApplicantSignature);
