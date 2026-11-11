namespace Praxis.Domain.Security;

/// <summary>Signed historical recovery ciphertext, never a decryption credential.</summary>
public sealed record OwnerRecoveryArchive(
    Guid SpaceId, long Epoch, Guid SignerDeviceId, byte[] KeyCommitment,
    RecoveryKeyEnvelope Recovery);
