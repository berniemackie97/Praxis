namespace Praxis.Domain.Security;

/// <summary>A signed, opaque key distribution submitted by an authorized owner device.</summary>
public sealed record SpaceKeyPublication(
    Guid SpaceId,
    long Epoch,
    Guid SignerDeviceId,
    byte[] KeyCommitment,
    IReadOnlyList<DeviceKeyEnvelope> Recipients,
    RecoveryKeyEnvelope Recovery);
