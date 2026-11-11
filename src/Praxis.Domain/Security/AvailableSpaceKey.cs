namespace Praxis.Domain.Security;

/// <summary>
/// Returned only to an enrolled device authorized for the selected security space.
/// Contains no plaintext collection key and no other members' recipient envelopes.
/// </summary>
public sealed record AvailableSpaceKey(
    Guid SpaceId,
    long Epoch,
    Guid SignerDeviceId,
    byte[] KeyCommitment,
    DeviceKeyEnvelope DeviceKey,
    RecoveryKeyEnvelope? OwnerRecovery);
