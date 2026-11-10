using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Praxis.Storage.Security;

public sealed record DeviceApprovalChallenge(Guid ChallengeId, Guid DeviceId, byte[] Message,
    DateTimeOffset ExpiresUtc);

/// <summary>A signed approval. A client must verify this against an independently pinned owner key.</summary>
public sealed record ApprovedDeviceIdentity(
    Guid InstallationId, Guid ChallengeId, Guid DeviceId, Guid SpaceId, Guid PrincipalId,
    Guid ApproverDeviceId, byte[] SigningPublicKey, byte[] WrappingPublicKey,
    byte[] WrappingSignature, byte[] Nonce, DateTimeOffset ExpiresUtc,
    byte[] OwnerSignature, byte[] ApplicantSignature);

/// <summary>
/// Owner-controlled device admission. This is not an unauthenticated network enrollment
/// API or an identity-provider account registration endpoint.
/// </summary>
public sealed class ApprovedDeviceEnrollment(SecurityRegistry registry)
{
    private const int MaxOutstanding = 8;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    public DeviceApprovalChallenge Begin(VerifiedDeviceProof ownerProof, Guid targetPrincipal,
        ReadOnlySpan<byte> signingPublicKey, ReadOnlySpan<byte> wrappingPublicKey,
        ReadOnlySpan<byte> wrappingBindingSignature)
    {
        ArgumentNullException.ThrowIfNull(ownerProof);
        byte[] signing = signingPublicKey.ToArray();
        byte[] wrapping = wrappingPublicKey.ToArray();
        byte[] binding = wrappingBindingSignature.ToArray();
        if (signing.Length is < 80 or > 128 ||
            !DeviceWrappingKey.Verify(signing, wrapping, binding))
        {
            throw new ArgumentException("The proposed device keys must be independently bound.");
        }

        return registry.Execute(connection =>
        {
            RequireOwner(connection, ownerProof);
            using SqliteTransaction tx = connection.BeginTransaction();
            if (!IsAuthorizedTarget(connection, tx, ownerProof, targetPrincipal))
            {
                throw new UnauthorizedAccessException("Device enrollment requires owner-approved membership.");
            }

            DateTimeOffset now = registry.UtcNow;
            using (SqliteCommand clean = SecurityRegistry.Command(connection, tx,
                "DELETE FROM pending_device_approvals WHERE consumed=1 OR expires_utc <= $now",
                ("$now", now.UtcDateTime.ToString("O"))))
            {
                clean.ExecuteNonQuery();
            }

            using (SqliteCommand count = SecurityRegistry.Command(connection, tx,
                "SELECT COUNT(*) FROM pending_device_approvals WHERE owner_device_id=$owner",
                ("$owner", SecurityRegistry.Id(ownerProof.DeviceId))))
            {
                if (Convert.ToInt64(count.ExecuteScalar()) >= MaxOutstanding)
                {
                    throw new InvalidOperationException("Too many outstanding enrollment approvals.");
                }
            }

            Guid challengeId = Guid.NewGuid();
            Guid deviceId = Guid.NewGuid();
            byte[] nonce = RandomNumberGenerator.GetBytes(32);
            DateTimeOffset expires = now.Add(Lifetime);
            using (SqliteCommand add = SecurityRegistry.Command(connection, tx, """
                INSERT INTO pending_device_approvals
                    (challenge_id, device_id, space_id, owner_device_id, principal_id,
                     signing_public_key, wrapping_public_key, wrapping_signature,
                     nonce, expires_utc)
                VALUES ($id, $device, $space, $owner, $principal, $signing, $wrapping, $binding,
                        $nonce, $expires)
                """,
                ("$id", SecurityRegistry.Id(challengeId)),
                ("$device", SecurityRegistry.Id(deviceId)),
                ("$space", SecurityRegistry.Id(ownerProof.SpaceId)),
                ("$owner", SecurityRegistry.Id(ownerProof.DeviceId)),
                ("$principal", SecurityRegistry.Id(targetPrincipal)),
                ("$signing", signing), ("$wrapping", wrapping), ("$binding", binding),
                ("$nonce", nonce), ("$expires", expires.UtcDateTime.ToString("O"))))
            {
                add.ExecuteNonQuery();
            }

            tx.Commit();
            return new DeviceApprovalChallenge(challengeId, deviceId,
                Message(registry.InstallationId, challengeId, deviceId,
                    ownerProof.SpaceId, targetPrincipal, ownerProof.DeviceId,
                    signing, wrapping, binding, nonce, expires), expires);
        });
    }

    public ApprovedDeviceIdentity? Complete(VerifiedDeviceProof ownerProof, Guid challengeId,
        ReadOnlySpan<byte> ownerSignature, ReadOnlySpan<byte> applicantSignature)
    {
        ArgumentNullException.ThrowIfNull(ownerProof);
        if (challengeId == Guid.Empty || ownerSignature.Length != 64 ||
            applicantSignature.Length != 64)
        {
            return null;
        }

        byte[] approvedSignature = ownerSignature.ToArray();
        byte[] applicant = applicantSignature.ToArray();
        return registry.Execute(connection =>
        {
            RequireOwner(connection, ownerProof);
            using SqliteTransaction tx = connection.BeginTransaction();
            Guid deviceId, spaceId, ownerDeviceId, principalId;
            byte[] signing, wrapping, binding, nonce;
            DateTimeOffset expires;

            using (SqliteCommand read = SecurityRegistry.Command(connection, tx, """
                SELECT device_id, space_id, owner_device_id, principal_id, signing_public_key,
                       wrapping_public_key, wrapping_signature, nonce, expires_utc
                FROM pending_device_approvals
                WHERE challenge_id=$id AND consumed=0
                """, ("$id", SecurityRegistry.Id(challengeId))))
            using (SqliteDataReader reader = read.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return null;
                }

                deviceId = Guid.Parse(reader.GetString(0));
                spaceId = Guid.Parse(reader.GetString(1));
                ownerDeviceId = Guid.Parse(reader.GetString(2));
                principalId = Guid.Parse(reader.GetString(3));
                signing = (byte[])reader.GetValue(4);
                wrapping = (byte[])reader.GetValue(5);
                binding = (byte[])reader.GetValue(6);
                nonce = (byte[])reader.GetValue(7);
                expires = DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture);
            }

            using (SqliteCommand consume = SecurityRegistry.Command(connection, tx,
                "UPDATE pending_device_approvals SET consumed=1 WHERE challenge_id=$id AND consumed=0",
                ("$id", SecurityRegistry.Id(challengeId))))
            {
                if (consume.ExecuteNonQuery() != 1)
                {
                    return null;
                }
            }

            byte[]? ownerKey = null;
            using (SqliteCommand owner = SecurityRegistry.Command(connection, tx, """
                SELECT public_key FROM devices WHERE device_id=$owner AND revoked_utc IS NULL
                """, ("$owner", SecurityRegistry.Id(ownerDeviceId))))
            {
                ownerKey = owner.ExecuteScalar() as byte[];
            }

            byte[] message = Message(registry.InstallationId, challengeId, deviceId,
                spaceId, principalId, ownerDeviceId, signing, wrapping, binding, nonce, expires);
            bool authorized = expires > registry.UtcNow &&
                spaceId == ownerProof.SpaceId &&
                ownerDeviceId == ownerProof.DeviceId &&
                IsAuthorizedTarget(connection, tx, ownerProof, principalId) &&
                ownerKey is not null &&
                VerifySignature(ownerKey, message, approvedSignature) &&
                VerifySignature(signing, message, applicant);

            if (!authorized)
            {
                tx.Commit(); // A failed challenge is still consumed; its signatures cannot be replayed.
                return null;
            }

            using (SqliteCommand add = SecurityRegistry.Command(connection, tx, """
                INSERT INTO devices
                    (device_id, principal_id, public_key, wrapping_public_key, wrapping_signature,
                     enrolled_utc)
                SELECT $device, principal_id, $signing, $wrapping, $binding, $now
                FROM principals WHERE principal_id=$principal AND disabled_utc IS NULL
                  AND NOT EXISTS (SELECT 1 FROM devices WHERE public_key=$signing)
                """,
                ("$device", SecurityRegistry.Id(deviceId)),
                ("$principal", SecurityRegistry.Id(principalId)),
                ("$signing", signing), ("$wrapping", wrapping), ("$binding", binding),
                ("$now", registry.UtcNow.UtcDateTime.ToString("O"))))
            {
                if (add.ExecuteNonQuery() != 1)
                {
                    tx.Commit();
                    return null;
                }
            }

            using (SqliteCommand save = SecurityRegistry.Command(connection, tx, """
                INSERT INTO device_approvals
                    (device_id, space_id, approved_by_device_id, approved_principal_id,
                     challenge_id, nonce, expires_utc, owner_signature, applicant_signature)
                VALUES ($device, $space, $owner, $principal, $challenge, $nonce, $expires,
                        $owner_signature, $applicant_signature)
                """,
                ("$device", SecurityRegistry.Id(deviceId)),
                ("$space", SecurityRegistry.Id(spaceId)),
                ("$owner", SecurityRegistry.Id(ownerDeviceId)),
                ("$principal", SecurityRegistry.Id(principalId)),
                ("$challenge", SecurityRegistry.Id(challengeId)),
                ("$nonce", nonce), ("$expires", expires.UtcDateTime.ToString("O")),
                ("$owner_signature", approvedSignature),
                ("$applicant_signature", applicant)))
            {
                save.ExecuteNonQuery();
            }

            SecurityOperator.RequireRekeyForPrincipal(connection, tx, principalId);
            tx.Commit();
            return new ApprovedDeviceIdentity(registry.InstallationId, challengeId, deviceId,
                spaceId, principalId, ownerDeviceId, signing, wrapping, binding, nonce,
                expires, approvedSignature, applicant);
        });
    }

    /// <summary>A signed receipt may be relayed; its bytes alone are not trusted.</summary>
    public ApprovedDeviceIdentity? Receipt(VerifiedDeviceProof ownerProof, Guid deviceId)
    {
        ArgumentNullException.ThrowIfNull(ownerProof);
        return registry.Execute(connection =>
        {
            RequireOwner(connection, ownerProof);
            using SqliteCommand query = SecurityRegistry.Command(connection, null, """
                SELECT a.challenge_id, a.space_id, a.approved_principal_id,
                       a.approved_by_device_id, d.public_key, d.wrapping_public_key,
                       d.wrapping_signature, a.nonce, a.expires_utc,
                       a.owner_signature, a.applicant_signature
                FROM device_approvals a JOIN devices d ON d.device_id=a.device_id
                WHERE a.device_id=$device AND a.space_id=$space
                """,
                ("$device", SecurityRegistry.Id(deviceId)),
                ("$space", SecurityRegistry.Id(ownerProof.SpaceId)));
            using SqliteDataReader reader = query.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new ApprovedDeviceIdentity(registry.InstallationId,
                Guid.Parse(reader.GetString(0)), deviceId, Guid.Parse(reader.GetString(1)),
                Guid.Parse(reader.GetString(2)), Guid.Parse(reader.GetString(3)),
                (byte[])reader.GetValue(4), (byte[])reader.GetValue(5),
                (byte[])reader.GetValue(6), (byte[])reader.GetValue(7),
                DateTimeOffset.Parse(reader.GetString(8), CultureInfo.InvariantCulture),
                (byte[])reader.GetValue(9), (byte[])reader.GetValue(10));
        });
    }

    /// <summary>
    /// Check against a separately pinned owner signing key. A host-supplied key is
    /// not an acceptable trust anchor for this check.
    /// </summary>
    public static bool VerifyApproval(ApprovedDeviceIdentity record,
        ReadOnlySpan<byte> independentlyPinnedOwnerKey, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(record);
        byte[] message = Message(record.InstallationId, record.ChallengeId,
            record.DeviceId, record.SpaceId, record.PrincipalId, record.ApproverDeviceId,
            record.SigningPublicKey, record.WrappingPublicKey, record.WrappingSignature,
            record.Nonce, record.ExpiresUtc);
        return record.ExpiresUtc > now &&
            record.Nonce.Length == 32 &&
            DeviceWrappingKey.Verify(record.SigningPublicKey, record.WrappingPublicKey,
                record.WrappingSignature) &&
            VerifySignature(independentlyPinnedOwnerKey, message, record.OwnerSignature) &&
            VerifySignature(record.SigningPublicKey, message, record.ApplicantSignature);
    }

    private void RequireOwner(SqliteConnection connection, VerifiedDeviceProof proof)
    {
        if (proof.Permission != SecurityPermission.Write || proof.SourceId is not null ||
            !registry.DeviceProofs.IsCurrent(proof))
        {
            throw new UnauthorizedAccessException("A current owner-space write proof is required.");
        }

        using SqliteCommand check = SecurityRegistry.Command(connection, null, """
            SELECT 1 FROM security_spaces WHERE space_id=$space AND owner_id=$principal
            """,
            ("$space", SecurityRegistry.Id(proof.SpaceId)),
            ("$principal", SecurityRegistry.Id(proof.PrincipalId)));
        if (check.ExecuteScalar() is null)
        {
            throw new UnauthorizedAccessException("Only the security-space owner may admit a device.");
        }
    }

    private static bool IsAuthorizedTarget(SqliteConnection connection,
        SqliteTransaction transaction, VerifiedDeviceProof owner, Guid principal)
    {
        using SqliteCommand check = SecurityRegistry.Command(connection, transaction, """
            SELECT 1 FROM principals p
            JOIN security_spaces s ON s.space_id=$space AND s.owner_id=$owner
            LEFT JOIN space_grants g ON g.space_id=s.space_id AND g.principal_id=p.principal_id
            WHERE p.principal_id=$principal AND p.disabled_utc IS NULL
              AND (p.principal_id=s.owner_id OR
                   (g.revoked_utc IS NULL AND (g.permissions & 1)=1))
            """,
            ("$space", SecurityRegistry.Id(owner.SpaceId)),
            ("$owner", SecurityRegistry.Id(owner.PrincipalId)),
            ("$principal", SecurityRegistry.Id(principal)));
        return check.ExecuteScalar() is not null;
    }

    private static byte[] Message(Guid installation, Guid challenge, Guid device, Guid space,
        Guid target, Guid owner, byte[] signing, byte[] wrapping, byte[] binding,
        byte[] nonce, DateTimeOffset expires) =>
        Encoding.UTF8.GetBytes("Praxis.DeviceApproval.v1\n" +
            $"{installation:N}\n{challenge:N}\n{device:N}\n{space:N}\n{target:N}\n{owner:N}\n" +
            $"{Convert.ToBase64String(signing)}\n{Convert.ToBase64String(wrapping)}\n" +
            $"{Convert.ToBase64String(binding)}\n{expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}\n" +
            $"{Convert.ToBase64String(nonce)}\n");

    private static bool VerifySignature(ReadOnlySpan<byte> publicKey, byte[] message,
        ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length is < 80 or > 128 || signature.Length != 64)
        {
            return false;
        }

        using ECDsa key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(publicKey, out int read);
        return read == publicKey.Length && key.KeySize == 256 &&
            key.VerifyData(message, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }
}
