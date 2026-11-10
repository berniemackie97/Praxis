using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Praxis.Storage.Security;

/// <summary>
/// Client-side envelope operations. Only public material and ciphertext may reach
/// the security host; content keys, recovery secrets and private keys remain local.
/// </summary>
public static class ClientSpaceKeys
{
    public static byte[] GenerateContentKey() => RandomNumberGenerator.GetBytes(32);
    public static byte[] GenerateRecoverySecret() => RandomNumberGenerator.GetBytes(32);

    public static SpaceKeyPublication Protect(Guid installationId, Guid spaceId, long epoch,
        Guid signerDeviceId, ECDsa signer, IReadOnlyList<RecipientWrappingKey> recipients,
        ReadOnlySpan<byte> contentKey, ReadOnlySpan<byte> recoverySecret)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(recipients);
        if (installationId == Guid.Empty || spaceId == Guid.Empty || signerDeviceId == Guid.Empty ||
            epoch < 1 || contentKey.Length != 32 || recoverySecret.Length != 32 ||
            signer.KeySize != 256 || recipients.Count is < 1 or > 64)
        {
            throw new ArgumentException("The key distribution parameters are invalid.");
        }

        byte[] commitment = SHA256.HashData(contentKey);
        List<DeviceKeyEnvelope> packages = new(recipients.Count);
        HashSet<Guid> deviceIds = new();
        foreach (RecipientWrappingKey target in recipients.OrderBy(r => r.DeviceId))
        {
            if (target.DeviceId == Guid.Empty || !deviceIds.Add(target.DeviceId) ||
                !DeviceWrappingKey.Verify(target.SigningPublicKey, target.PublicKey, target.BindingSignature))
            {
                throw new ArgumentException("The recipient key is not uniquely attested.");
            }

            using RSA rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(target.PublicKey, out int bytesRead);
            if (bytesRead != target.PublicKey.Length || rsa.KeySize != 3072)
            {
                throw new ArgumentException("A 3072-bit RSA recipient key is required.");
            }

            byte[] wrappedKey = rsa.Encrypt(contentKey, RSAEncryptionPadding.OaepSHA256);
            byte[] signature = Sign(signer, DeviceMessage(installationId, spaceId, epoch,
                signerDeviceId, target.DeviceId, commitment, wrappedKey));
            packages.Add(new DeviceKeyEnvelope(target.DeviceId, wrappedKey, signature));
        }

        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] encrypted = new byte[48];
        byte[] associated = RecoveryContext(installationId, spaceId, epoch, signerDeviceId, commitment);
        using (AesGcm aes = new(recoverySecret, tagSizeInBytes: 16))
        {
            aes.Encrypt(nonce, contentKey, encrypted.AsSpan(0, 32),
                encrypted.AsSpan(32, 16), associated);
        }

        byte[] signedRecovery = Sign(signer, RecoveryMessage(
            installationId, spaceId, epoch, signerDeviceId, commitment, nonce, encrypted));
        return new SpaceKeyPublication(spaceId, epoch, signerDeviceId, commitment,
            packages, new RecoveryKeyEnvelope(nonce, encrypted, signedRecovery));
    }

    public static byte[] UnlockForDevice(Guid installationId, AvailableSpaceKey offered,
        RSA recipientPrivateKey, ECDsa trustedSignerPublicKey, long minimumEpoch)
    {
        ArgumentNullException.ThrowIfNull(offered);
        ValidateEpoch(offered, minimumEpoch);
        ArgumentNullException.ThrowIfNull(recipientPrivateKey);
        ArgumentNullException.ThrowIfNull(trustedSignerPublicKey);
        DeviceKeyEnvelope envelope = offered.DeviceKey;
        if (!Verify(trustedSignerPublicKey, DeviceMessage(installationId, offered.SpaceId,
                offered.Epoch, offered.SignerDeviceId, envelope.DeviceId, offered.KeyCommitment,
                envelope.WrappedKey), envelope.Signature))
        {
            throw new CryptographicException("The device key envelope is not authenticated.");
        }

        byte[] key = recipientPrivateKey.Decrypt(envelope.WrappedKey, RSAEncryptionPadding.OaepSHA256);
        VerifyCommitment(key, offered.KeyCommitment);
        return key;
    }

    public static byte[] UnlockWithRecovery(Guid installationId, AvailableSpaceKey offered,
        ReadOnlySpan<byte> recoverySecret, ECDsa trustedSignerPublicKey, long minimumEpoch)
    {
        ArgumentNullException.ThrowIfNull(offered);
        if (offered.OwnerRecovery is null)
        {
            throw new CryptographicException("The owner recovery material is unavailable.");
        }

        return UnlockWithRecovery(installationId,
            new OwnerRecoveryArchive(offered.SpaceId, offered.Epoch, offered.SignerDeviceId,
                offered.KeyCommitment, offered.OwnerRecovery),
            recoverySecret, trustedSignerPublicKey, minimumEpoch);
    }

    public static byte[] UnlockWithRecovery(Guid installationId, OwnerRecoveryArchive offered,
        ReadOnlySpan<byte> recoverySecret, ECDsa trustedSignerPublicKey, long minimumEpoch)
    {
        ArgumentNullException.ThrowIfNull(offered);
        if (offered.Epoch < 1 || minimumEpoch < 1 || offered.Epoch < minimumEpoch)
        {
            throw new CryptographicException("The key epoch is older than trusted client state.");
        }

        ArgumentNullException.ThrowIfNull(trustedSignerPublicKey);
        if (recoverySecret.Length != 32)
        {
            throw new CryptographicException("The owner recovery material is unavailable.");
        }

        RecoveryKeyEnvelope envelope = offered.Recovery;
        if (envelope.Nonce.Length != 12 || envelope.CiphertextAndTag.Length != 48 ||
            !Verify(trustedSignerPublicKey, RecoveryMessage(installationId, offered.SpaceId,
                offered.Epoch, offered.SignerDeviceId, offered.KeyCommitment,
                envelope.Nonce, envelope.CiphertextAndTag), envelope.Signature))
        {
            throw new CryptographicException("The recovery material is not authenticated.");
        }

        byte[] key = new byte[32];
        try
        {
            using AesGcm aes = new(recoverySecret, tagSizeInBytes: 16);
            aes.Decrypt(envelope.Nonce, envelope.CiphertextAndTag.AsSpan(0, 32),
                envelope.CiphertextAndTag.AsSpan(32, 16), key,
                RecoveryContext(installationId, offered.SpaceId, offered.Epoch,
                    offered.SignerDeviceId, offered.KeyCommitment));
            VerifyCommitment(key, offered.KeyCommitment);
            return key;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    internal static byte[] DeviceMessage(Guid installation, Guid spaceId, long epoch,
        Guid signerDevice, Guid recipientDevice, ReadOnlySpan<byte> commitment,
        ReadOnlySpan<byte> encrypted)
    {
        return Encoding.ASCII.GetBytes("Praxis.DeviceSpaceKey.v1\n" +
            $"{installation:N}\n{spaceId:N}\n{epoch.ToString(CultureInfo.InvariantCulture)}\n" +
            $"{signerDevice:N}\n{recipientDevice:N}\n" +
            $"{Convert.ToHexString(commitment)}\n{Convert.ToBase64String(encrypted)}\n");
    }

    internal static byte[] RecoveryMessage(Guid installation, Guid spaceId, long epoch,
        Guid signerDevice, ReadOnlySpan<byte> commitment, ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> encrypted)
    {
        return Encoding.ASCII.GetBytes("Praxis.RecoveryEnvelope.v1\n" +
            $"{installation:N}\n{spaceId:N}\n{epoch.ToString(CultureInfo.InvariantCulture)}\n" +
            $"{signerDevice:N}\n{Convert.ToHexString(commitment)}\n" +
            $"{Convert.ToBase64String(nonce)}\n{Convert.ToBase64String(encrypted)}\n");
    }

    private static byte[] RecoveryContext(Guid installation, Guid spaceId, long epoch,
        Guid signerDevice, ReadOnlySpan<byte> commitment) =>
        Encoding.ASCII.GetBytes("Praxis.RecoveryKey.v1\n" +
            $"{installation:N}\n{spaceId:N}\n{epoch.ToString(CultureInfo.InvariantCulture)}\n" +
            $"{signerDevice:N}\n{Convert.ToHexString(commitment)}\n");

    private static void ValidateEpoch(AvailableSpaceKey offered, long minimumEpoch)
    {
        if (offered.Epoch < 1 || minimumEpoch < 1 || offered.Epoch < minimumEpoch)
        {
            throw new CryptographicException("The key epoch is older than the trusted client state.");
        }
    }

    internal static bool Verify(ECDsa signer, ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> signature) =>
        signature.Length == 64 &&
        signer.VerifyData(message, signature, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private static byte[] Sign(ECDsa signer, ReadOnlySpan<byte> message) =>
        signer.SignData(message, HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

    private static void VerifyCommitment(byte[] key, ReadOnlySpan<byte> commitment)
    {
        if (key.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(SHA256.HashData(key), commitment))
        {
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException("The recovered key does not match its commitment.");
        }
    }
}
