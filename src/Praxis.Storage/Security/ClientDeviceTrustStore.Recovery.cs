using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Praxis.Storage.Security;

public sealed partial class ClientDeviceTrustStore
{
    /// <summary>A separate recovery signer must be verified independently of the host.</summary>
    public static byte[] RecoveryAuthorityMessage(Guid installation, Guid ownerDevice,
        long version, ReadOnlySpan<byte> recoverySigningPublicKey) =>
        Encoding.UTF8.GetBytes("Praxis.ClientRecoveryAuthority.v1\n" +
            $"{installation:N}\n{ownerDevice:N}\n" +
            $"{version.ToString(CultureInfo.InvariantCulture)}\n" +
            $"{Convert.ToBase64String(recoverySigningPublicKey)}\n");

    public void RegisterRecoveryAuthority(ReadOnlySpan<byte> recoverySigningPublicKey,
        ReadOnlySpan<byte> ownerSignature, ReadOnlySpan<byte> newRecoverySignature,
        ReadOnlySpan<byte> previousRecoverySignature = default)
    {
        byte[] proposed = recoverySigningPublicKey.ToArray();
        byte[] ownerSigned = ownerSignature.ToArray();
        byte[] newSigned = newRecoverySignature.ToArray();
        byte[] oldSigned = previousRecoverySignature.ToArray();
        lock (gate)
        {
            byte[]? previous = null;
            long version = 1;
            using (SqliteCommand read = SecurityRegistry.Command(connection, null,
                "SELECT public_key, version FROM recovery_authority WHERE singleton=1"))
            using (SqliteDataReader reader = read.ExecuteReader())
            {
                if (reader.Read())
                {
                    previous = (byte[])reader.GetValue(0);
                    version = checked(reader.GetInt64(1) + 1);
                }
            }

            byte[] message = RecoveryAuthorityMessage(installationId, ownerDeviceId,
                version, proposed);
            if (!ValidSignature(ownerPublicKey, message, ownerSigned) ||
                !ValidSignature(proposed, message, newSigned) ||
                (previous is not null && !ValidSignature(previous, message, oldSigned)))
            {
                throw new UnauthorizedAccessException("Independent owner and recovery signatures are required.");
            }

            using SqliteCommand update = SecurityRegistry.Command(connection, null, """
                INSERT INTO recovery_authority (singleton, public_key, version)
                VALUES (1, $key, $version)
                ON CONFLICT(singleton) DO UPDATE SET
                    public_key=excluded.public_key, version=excluded.version
                """, ("$key", proposed), ("$version", version));
            update.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// A recovery-signing credential and the replacement device both authorize the
    /// transfer. Existing recipient pins are cleared; clients must reapprove sharing.
    /// </summary>
    public static byte[] OwnerTransferMessage(Guid installation, Guid previousOwnerDevice,
        ReadOnlySpan<byte> previousOwnerPublicKey, Guid replacementOwnerDevice,
        ReadOnlySpan<byte> replacementOwnerPublicKey, long recoveryVersion) =>
        Encoding.UTF8.GetBytes("Praxis.ClientOwnerTransfer.v1\n" +
            $"{installation:N}\n{previousOwnerDevice:N}\n" +
            $"{Convert.ToBase64String(previousOwnerPublicKey)}\n" +
            $"{replacementOwnerDevice:N}\n{Convert.ToBase64String(replacementOwnerPublicKey)}\n" +
            $"{recoveryVersion.ToString(CultureInfo.InvariantCulture)}\n");

    public void TransferOwner(Guid replacementOwnerDevice,
        ReadOnlySpan<byte> replacementOwnerSigningPublicKey,
        ReadOnlySpan<byte> recoverySignature, ReadOnlySpan<byte> replacementSignature)
    {
        if (replacementOwnerDevice == Guid.Empty || replacementOwnerDevice == ownerDeviceId)
        {
            throw new ArgumentException("The replacement must be a distinct enrolled device.");
        }

        byte[] next = replacementOwnerSigningPublicKey.ToArray();
        byte[] recoverySigned = recoverySignature.ToArray();
        byte[] nextSigned = replacementSignature.ToArray();
        lock (gate)
        {
            byte[] recoveryKey;
            long recoveryVersion;
            using (SqliteCommand read = SecurityRegistry.Command(connection, null,
                "SELECT public_key, version FROM recovery_authority WHERE singleton=1"))
            using (SqliteDataReader reader = read.ExecuteReader())
            {
                if (!reader.Read())
                {
                    throw new UnauthorizedAccessException("A previously pinned recovery key is required.");
                }

                recoveryKey = (byte[])reader.GetValue(0);
                recoveryVersion = reader.GetInt64(1);
            }

            byte[] message = OwnerTransferMessage(installationId, ownerDeviceId,
                ownerPublicKey, replacementOwnerDevice, next, recoveryVersion);
            if (!ValidSignature(recoveryKey, message, recoverySigned) ||
                !ValidSignature(next, message, nextSigned))
            {
                throw new UnauthorizedAccessException("The recovery signer and replacement device must approve transfer.");
            }

            using SqliteTransaction tx = connection.BeginTransaction();
            using (SqliteCommand update = SecurityRegistry.Command(connection, tx, """
                UPDATE owner_identity
                SET owner_device_id=$device, public_key=$key
                WHERE singleton=1 AND owner_device_id=$previous AND public_key=$old
                """,
                ("$device", SecurityRegistry.Id(replacementOwnerDevice)),
                ("$key", next), ("$previous", SecurityRegistry.Id(ownerDeviceId)),
                ("$old", ownerPublicKey)))
            {
                if (update.ExecuteNonQuery() != 1)
                {
                    throw new InvalidDataException("The previously pinned owner has changed.");
                }
            }

            using (SqliteCommand clear = SecurityRegistry.Command(connection, tx,
                "DELETE FROM device_pins"))
            {
                clear.ExecuteNonQuery();
            }

            tx.Commit();
            ownerDeviceId = replacementOwnerDevice;
            ownerPublicKey = next;
        }
    }

    private static bool ValidSignature(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message,
        ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length is < 80 or > 128 || signature.Length != 64)
        {
            return false;
        }

        try
        {
            using ECDsa verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(publicKey, out int read);
            return read == publicKey.Length && verifier.KeySize == 256 &&
                verifier.ExportParameters(false).Curve.Oid.Value == "1.2.840.10045.3.1.7" &&
                verifier.VerifyData(message, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }
}
