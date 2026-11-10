using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Praxis.Storage.Security;

public sealed record ReplacementChallenge(Guid EnrollmentId, byte[] Message, DateTimeOffset ExpiresUtc);

/// <summary>
/// Separately held P-256 recovery credential for replacing a lost signing device.
/// It never contains the content recovery secret or a decrypting private key.
/// </summary>
public sealed class RecoveryEnrollment(SecurityRegistry registry)
{
    private const int MaxOutstanding = 8;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    public static byte[] RegistrationMessage(Guid installation, Guid principal, Guid ownerDevice,
        long version, ReadOnlySpan<byte> recoveryPublicKey)
    {
        if (installation == Guid.Empty || principal == Guid.Empty || ownerDevice == Guid.Empty ||
            version < 1 || recoveryPublicKey.IsEmpty)
        {
            throw new ArgumentException("Invalid recovery registration identity.");
        }

        return Encoding.UTF8.GetBytes("Praxis.RecoveryRegistration.v1\n" +
            $"{installation:N}\n{principal:N}\n{ownerDevice:N}\n" +
            $"{version.ToString(CultureInfo.InvariantCulture)}\n" +
            $"{Convert.ToBase64String(recoveryPublicKey)}\n");
    }

    /// <summary>
    /// Registration requires proof by an enrolled owner device AND a separate recovery key;
    /// replacing it additionally requires proof by the previous recovery key.
    /// </summary>
    public void Register(VerifiedDeviceProof ownerProof, ReadOnlySpan<byte> recoveryPublicKey,
        ReadOnlySpan<byte> ownerSignature, ReadOnlySpan<byte> recoverySignature,
        ReadOnlySpan<byte> previousRecoverySignature = default)
    {
        ArgumentNullException.ThrowIfNull(ownerProof);
        byte[] recoveryKey = recoveryPublicKey.ToArray();
        byte[] deviceSigned = ownerSignature.ToArray();
        byte[] newSigned = recoverySignature.ToArray();
        byte[] oldSigned = previousRecoverySignature.ToArray();
        ValidateSigningKey(recoveryKey);

        registry.Execute(connection =>
        {
            if (!registry.DeviceProofs.IsCurrent(ownerProof) ||
                ownerProof.Permission != SecurityPermission.Write || ownerProof.SourceId is not null)
            {
                throw new UnauthorizedAccessException("A current owner-space write proof is required.");
            }

            using SqliteTransaction tx = connection.BeginTransaction();
            byte[] ownerKey;
            using (SqliteCommand owner = SecurityRegistry.Command(connection, tx, """
                SELECT d.public_key FROM devices d
                JOIN security_spaces s ON s.owner_id=d.principal_id AND s.space_id=$space
                WHERE d.device_id=$device AND d.principal_id=$principal
                  AND d.revoked_utc IS NULL
                """,
                ("$space", SecurityRegistry.Id(ownerProof.SpaceId)),
                ("$device", SecurityRegistry.Id(ownerProof.DeviceId)),
                ("$principal", SecurityRegistry.Id(ownerProof.PrincipalId))))
            {
                ownerKey = owner.ExecuteScalar() as byte[] ??
                    throw new UnauthorizedAccessException("The enrolling device must own the space.");
            }

            byte[]? previousKey = null;
            long version = 1;
            using (SqliteCommand current = SecurityRegistry.Command(connection, tx, """
                SELECT public_key, version FROM principal_recovery WHERE principal_id=$principal
                """, ("$principal", SecurityRegistry.Id(ownerProof.PrincipalId))))
            using (SqliteDataReader reader = current.ExecuteReader())
            {
                if (reader.Read())
                {
                    previousKey = (byte[])reader.GetValue(0);
                    version = checked(reader.GetInt64(1) + 1);
                }
            }

            byte[] message = RegistrationMessage(registry.InstallationId,
                ownerProof.PrincipalId, ownerProof.DeviceId, version, recoveryKey);
            if (!Verify(ownerKey, message, deviceSigned) || !Verify(recoveryKey, message, newSigned) ||
                (previousKey is not null && !Verify(previousKey, message, oldSigned)))
            {
                throw new UnauthorizedAccessException("The owner and recovery credentials must authorize registration.");
            }

            using (SqliteCommand save = SecurityRegistry.Command(connection, tx, """
                INSERT INTO principal_recovery (principal_id, public_key, version, registered_utc)
                VALUES ($principal, $key, $version, $now)
                ON CONFLICT(principal_id) DO UPDATE SET
                    public_key=excluded.public_key, version=excluded.version,
                    registered_utc=excluded.registered_utc
                """,
                ("$principal", SecurityRegistry.Id(ownerProof.PrincipalId)),
                ("$key", recoveryKey), ("$version", version),
                ("$now", registry.UtcNow.UtcDateTime.ToString("O"))))
            {
                save.ExecuteNonQuery();
            }

            using (SqliteCommand invalidate = SecurityRegistry.Command(connection, tx,
                "DELETE FROM recovery_enrollments WHERE principal_id=$principal",
                ("$principal", SecurityRegistry.Id(ownerProof.PrincipalId))))
            {
                invalidate.ExecuteNonQuery();
            }

            tx.Commit();
            return 0;
        });
    }

    /// <summary>
    /// Public request preparation gives no authority. Completion requires both the
    /// independent recovery signer and the replacement device's signing private key.
    /// Never publish this as an unauthenticated endpoint without independent rate limits.
    /// </summary>
    public ReplacementChallenge? Begin(Guid principalId, ReadOnlySpan<byte> signingPublicKey,
        ReadOnlySpan<byte> wrappingPublicKey, ReadOnlySpan<byte> wrappingBindingSignature)
    {
        string principal = SecurityRegistry.Id(principalId);
        byte[] signing = signingPublicKey.ToArray();
        byte[] wrapping = wrappingPublicKey.ToArray();
        byte[] binding = wrappingBindingSignature.ToArray();
        ValidateSigningKey(signing);
        if (!DeviceWrappingKey.Verify(signing, wrapping, binding))
        {
            throw new ArgumentException("The replacement recipient key must be signed by its device.");
        }

        return registry.Execute(connection =>
        {
            using SqliteTransaction tx = connection.BeginTransaction();
            long version;
            using (SqliteCommand current = SecurityRegistry.Command(connection, tx, """
                SELECT r.version FROM principal_recovery r
                JOIN principals p ON p.principal_id=r.principal_id AND p.disabled_utc IS NULL
                WHERE r.principal_id=$principal
                """, ("$principal", principal)))
            {
                if (current.ExecuteScalar() is not long v)
                {
                    return null;
                }

                version = v;
            }

            DateTimeOffset now = registry.UtcNow;
            using (SqliteCommand clean = SecurityRegistry.Command(connection, tx,
                "DELETE FROM recovery_enrollments WHERE consumed=1 OR expires_utc <= $now",
                ("$now", now.UtcDateTime.ToString("O"))))
            {
                clean.ExecuteNonQuery();
            }

            using (SqliteCommand count = SecurityRegistry.Command(connection, tx,
                "SELECT COUNT(*) FROM recovery_enrollments WHERE principal_id=$principal",
                ("$principal", principal)))
            {
                if (Convert.ToInt64(count.ExecuteScalar()) >= MaxOutstanding)
                {
                    throw new InvalidOperationException("Too many outstanding recovery enrollments.");
                }
            }

            Guid enrollmentId = Guid.NewGuid();
            byte[] nonce = RandomNumberGenerator.GetBytes(32);
            DateTimeOffset expires = now.Add(Lifetime);
            using (SqliteCommand add = SecurityRegistry.Command(connection, tx, """
                INSERT INTO recovery_enrollments
                    (enrollment_id, principal_id, signing_key, wrapping_key, wrapping_signature,
                     recovery_version, nonce, expires_utc)
                VALUES ($id, $principal, $signing, $wrapping, $binding, $version, $nonce, $expires)
                """,
                ("$id", SecurityRegistry.Id(enrollmentId)), ("$principal", principal),
                ("$signing", signing), ("$wrapping", wrapping), ("$binding", binding),
                ("$version", version), ("$nonce", nonce),
                ("$expires", expires.UtcDateTime.ToString("O"))))
            {
                add.ExecuteNonQuery();
            }

            tx.Commit();
            return new ReplacementChallenge(enrollmentId, Message(registry.InstallationId,
                enrollmentId, principalId, version, signing, wrapping, binding, nonce, expires), expires);
        });
    }

    public Guid? Complete(Guid enrollmentId, ReadOnlySpan<byte> recoverySignature,
        ReadOnlySpan<byte> replacementDeviceSignature)
    {
        if (enrollmentId == Guid.Empty || recoverySignature.Length != 64 ||
            replacementDeviceSignature.Length != 64)
        {
            return null;
        }

        byte[] recovered = recoverySignature.ToArray();
        byte[] deviceSigned = replacementDeviceSignature.ToArray();
        return registry.Execute(connection =>
        {
            using SqliteTransaction tx = connection.BeginTransaction();
            Guid principalId;
            byte[] recoveryKey, signing, wrapping, binding, nonce;
            DateTimeOffset expires;
            long version, currentVersion;
            using (SqliteCommand read = SecurityRegistry.Command(connection, tx, """
                SELECT e.principal_id, e.signing_key, e.wrapping_key, e.wrapping_signature,
                       e.recovery_version, e.nonce, e.expires_utc, r.public_key, r.version
                FROM recovery_enrollments e
                JOIN principals p ON p.principal_id=e.principal_id AND p.disabled_utc IS NULL
                JOIN principal_recovery r ON r.principal_id=e.principal_id
                WHERE e.enrollment_id=$id AND e.consumed=0
                """, ("$id", SecurityRegistry.Id(enrollmentId))))
            using (SqliteDataReader reader = read.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return null;
                }

                principalId = Guid.Parse(reader.GetString(0));
                signing = (byte[])reader.GetValue(1);
                wrapping = (byte[])reader.GetValue(2);
                binding = (byte[])reader.GetValue(3);
                version = reader.GetInt64(4);
                nonce = (byte[])reader.GetValue(5);
                expires = DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture);
                recoveryKey = (byte[])reader.GetValue(7);
                currentVersion = reader.GetInt64(8);
            }

            using (SqliteCommand consume = SecurityRegistry.Command(connection, tx, """
                UPDATE recovery_enrollments SET consumed=1
                WHERE enrollment_id=$id AND consumed=0
                """, ("$id", SecurityRegistry.Id(enrollmentId))))
            {
                if (consume.ExecuteNonQuery() != 1)
                {
                    return null;
                }
            }

            byte[] message = Message(registry.InstallationId, enrollmentId,
                principalId, version, signing, wrapping, binding, nonce, expires);
            if (version != currentVersion || expires <= registry.UtcNow ||
                !Verify(recoveryKey, message, recovered) ||
                !Verify(signing, message, deviceSigned))
            {
                tx.Commit(); // Consume even failed proofs; no replay of the same challenge.
                return null;
            }

            Guid deviceId = Guid.NewGuid();
            using (SqliteCommand enroll = SecurityRegistry.Command(connection, tx, """
                INSERT INTO devices
                    (device_id, principal_id, public_key, wrapping_public_key, wrapping_signature, enrolled_utc)
                SELECT $device, $principal, $signing, $wrapping, $binding, $now
                WHERE NOT EXISTS (SELECT 1 FROM devices WHERE public_key=$signing)
                """,
                ("$device", SecurityRegistry.Id(deviceId)),
                ("$principal", SecurityRegistry.Id(principalId)),
                ("$signing", signing), ("$wrapping", wrapping),
                ("$binding", binding),
                ("$now", registry.UtcNow.UtcDateTime.ToString("O"))))
            {
                if (enroll.ExecuteNonQuery() != 1)
                {
                    tx.Commit();
                    return null;
                }
            }

            SecurityOperator.RequireRekeyForPrincipal(connection, tx, principalId);
            tx.Commit();
            return deviceId;
        });
    }

    private static byte[] Message(Guid installation, Guid enrollment, Guid principal,
        long version, byte[] signing, byte[] wrapping, byte[] binding, byte[] nonce,
        DateTimeOffset expires) =>
        Encoding.UTF8.GetBytes("Praxis.ReplacementDevice.v1\n" +
            $"{installation:N}\n{enrollment:N}\n{principal:N}\n" +
            $"{version.ToString(CultureInfo.InvariantCulture)}\n" +
            $"{Convert.ToBase64String(signing)}\n{Convert.ToBase64String(wrapping)}\n" +
            $"{Convert.ToBase64String(binding)}\n{expires.ToUnixTimeSeconds()}\n" +
            $"{Convert.ToBase64String(nonce)}\n");

    private static void ValidateSigningKey(ReadOnlySpan<byte> key)
    {
        if (key.Length is < 80 or > 128)
        {
            throw new ArgumentException("A P-256 signing public key is required.");
        }

        using ECDsa ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(key, out int consumed);
        if (consumed != key.Length || ecdsa.KeySize != 256 ||
            ecdsa.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
        {
            throw new ArgumentException("A P-256 signing public key is required.");
        }
    }

    private static bool Verify(byte[] signingKey, byte[] message, byte[] signature)
    {
        if (signature.Length != 64)
        {
            return false;
        }

        using ECDsa signer = ECDsa.Create();
        signer.ImportSubjectPublicKeyInfo(signingKey, out int consumed);
        return consumed == signingKey.Length &&
            signer.VerifyData(message, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
