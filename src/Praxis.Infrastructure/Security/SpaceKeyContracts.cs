namespace Praxis.Infrastructure.Security;

public sealed record RecipientWrappingKey(Guid DeviceId, byte[] SigningPublicKey, byte[] PublicKey, byte[] BindingSignature);
public sealed record DeviceKeyEnvelope(Guid DeviceId, byte[] WrappedKey, byte[] Signature);
public sealed record RecoveryKeyEnvelope(byte[] Nonce, byte[] CiphertextAndTag, byte[] Signature);

/// <summary>Signed historical recovery ciphertext, never a decryption credential.</summary>
public sealed record OwnerRecoveryArchive(
    Guid SpaceId, long Epoch, Guid SignerDeviceId, byte[] KeyCommitment,
    RecoveryKeyEnvelope Recovery);

/// <summary>A signed, opaque key distribution submitted by an authorized owner device.</summary>
public sealed record SpaceKeyPublication(
    Guid SpaceId,
    long Epoch,
    Guid SignerDeviceId,
    byte[] KeyCommitment,
    IReadOnlyList<DeviceKeyEnvelope> Recipients,
    RecoveryKeyEnvelope Recovery);

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
