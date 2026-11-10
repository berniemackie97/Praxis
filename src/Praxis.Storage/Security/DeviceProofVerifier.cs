using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Praxis.Storage.Security;

/// <summary>A 30-second, single-use challenge for a specific device, security space and operation.</summary>
public sealed record DeviceChallenge(Guid ChallengeId, byte[] Message, DateTimeOffset ExpiresUtc);

/// <summary>
/// An authenticated request result, not a durable session or bearer token. Before every
/// data operation, callers must additionally check IsCurrent against the registry.
/// </summary>
public sealed class VerifiedDeviceProof
{
    internal VerifiedDeviceProof(Guid installationId, Guid principalId, Guid deviceId,
        Guid spaceId, Guid? sourceId, SecurityPermission permission, DateTimeOffset expiresUtc)
    {
        InstallationId = installationId;
        PrincipalId = principalId;
        DeviceId = deviceId;
        SpaceId = spaceId;
        SourceId = sourceId;
        Permission = permission;
        ExpiresUtc = expiresUtc;
    }

    internal Guid InstallationId { get; }
    public Guid PrincipalId { get; }
    public Guid DeviceId { get; }
    public Guid SpaceId { get; }
    public Guid? SourceId { get; }
    public SecurityPermission Permission { get; }
    public DateTimeOffset ExpiresUtc { get; }
}

/// <summary>
/// P-256 SHA-256 proof-of-possession for a single requested access. Not a general OAuth
/// implementation, bearer-token issuer, key-encryption mechanism or private-data API.
/// </summary>
public sealed class DeviceProofVerifier
{
    private const int MaxOutstanding = 16;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private readonly SecurityRegistry registry;

    internal DeviceProofVerifier(SecurityRegistry registry) => this.registry = registry;

    public DeviceChallenge IssueChallenge(Guid deviceId, Guid spaceId,
        Guid? sourceId, SecurityPermission permission)
    {
        ValidatePermission(permission);
        string device = SecurityRegistry.Id(deviceId);
        string space = SecurityRegistry.Id(spaceId);
        string? source = sourceId is null ? null : SecurityRegistry.Id(sourceId.Value);

        return registry.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            DateTimeOffset now = registry.UtcNow;
            using (SqliteCommand purge = SecurityRegistry.Command(connection, transaction,
                "DELETE FROM proof_challenges WHERE consumed=1 OR expires_utc <= $now",
                ("$now", Stamp(now))))
            {
                purge.ExecuteNonQuery();
            }

            if (!HasAccess(connection, transaction, device, space, source, permission))
            {
                throw new UnauthorizedAccessException("Device authorization is required.");
            }

            using (SqliteCommand count = SecurityRegistry.Command(connection, transaction, """
                SELECT COUNT(*) FROM proof_challenges WHERE device_id=$device
                """, ("$device", device)))
            {
                if (Convert.ToInt64(count.ExecuteScalar()) >= MaxOutstanding)
                {
                    throw new InvalidOperationException("Too many outstanding device challenges.");
                }
            }

            Guid challengeId = Guid.NewGuid();
            byte[] nonce = RandomNumberGenerator.GetBytes(32);
            DateTimeOffset expires = now.Add(Lifetime);
            using (SqliteCommand command = SecurityRegistry.Command(connection, transaction, """
                INSERT INTO proof_challenges
                    (challenge_id, device_id, space_id, source_id, permission, nonce, expires_utc)
                VALUES ($challenge, $device, $space, $source, $permission, $nonce, $expires)
                """,
                ("$challenge", SecurityRegistry.Id(challengeId)),
                ("$device", device),
                ("$space", space),
                ("$source", source),
                ("$permission", (int)permission),
                ("$nonce", nonce),
                ("$expires", Stamp(expires))))
            {
                command.ExecuteNonQuery();
            }

            transaction.Commit();
            return new DeviceChallenge(challengeId,
                Message(registry.InstallationId, challengeId, deviceId, spaceId,
                    sourceId, permission, nonce, expires), expires);
        });
    }

    public VerifiedDeviceProof? Authenticate(Guid challengeId, ReadOnlySpan<byte> signature)
    {
        if (challengeId == Guid.Empty || signature.Length != 64)
        {
            return null;
        }

        byte[] supplied = signature.ToArray();
        return registry.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            Guid deviceId, principalId, spaceId;
            Guid? sourceId;
            SecurityPermission permission;
            byte[] nonce, publicKey;
            DateTimeOffset expires;

            using (SqliteCommand load = SecurityRegistry.Command(connection, transaction, """
                SELECT c.device_id, d.principal_id, c.space_id, c.source_id, c.permission,
                       c.nonce, c.expires_utc, d.public_key
                FROM proof_challenges c
                JOIN devices d ON d.device_id=c.device_id
                WHERE c.challenge_id=$challenge AND c.consumed=0
                """, ("$challenge", SecurityRegistry.Id(challengeId))))
            using (SqliteDataReader reader = load.ExecuteReader())
            {
                if (!reader.Read())
                {
                    return null;
                }

                deviceId = Guid.Parse(reader.GetString(0));
                principalId = Guid.Parse(reader.GetString(1));
                spaceId = Guid.Parse(reader.GetString(2));
                sourceId = reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3));
                permission = (SecurityPermission)reader.GetInt32(4);
                nonce = (byte[])reader.GetValue(5);
                expires = DateTimeOffset.Parse(reader.GetString(6), System.Globalization.CultureInfo.InvariantCulture);
                publicKey = (byte[])reader.GetValue(7);
            }

            using (SqliteCommand consume = SecurityRegistry.Command(connection, transaction, """
                UPDATE proof_challenges SET consumed=1
                WHERE challenge_id=$challenge AND consumed=0
                """, ("$challenge", SecurityRegistry.Id(challengeId))))
            {
                if (consume.ExecuteNonQuery() != 1)
                {
                    return null;
                }
            }

            bool allowed = expires > registry.UtcNow &&
                HasAccess(connection, transaction, SecurityRegistry.Id(deviceId),
                    SecurityRegistry.Id(spaceId),
                    sourceId is null ? null : SecurityRegistry.Id(sourceId.Value),
                    permission);
            bool valid = false;
            if (allowed)
            {
                using ECDsa verifier = ECDsa.Create();
                verifier.ImportSubjectPublicKeyInfo(publicKey, out int read);
                valid = read == publicKey.Length && verifier.VerifyData(
                    Message(registry.InstallationId, challengeId, deviceId, spaceId,
                        sourceId, permission, nonce, expires),
                    supplied, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }

            transaction.Commit();
            return valid
                ? new VerifiedDeviceProof(registry.InstallationId, principalId, deviceId,
                    spaceId, sourceId, permission, expires)
                : null;
        });
    }

    /// <summary>
    /// Re-checks an authenticated request after revocation/grant changes. A future data
    /// transport must enforce this together with its own data authorization boundary.
    /// </summary>
    public bool IsCurrent(VerifiedDeviceProof proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        if (proof.InstallationId != registry.InstallationId || proof.ExpiresUtc <= registry.UtcNow)
        {
            return false;
        }

        return registry.Execute(connection =>
        {
            if (!HasAccess(connection, null, SecurityRegistry.Id(proof.DeviceId),
                SecurityRegistry.Id(proof.SpaceId),
                proof.SourceId is null ? null : SecurityRegistry.Id(proof.SourceId.Value),
                proof.Permission))
            {
                return false;
            }

            using SqliteCommand check = SecurityRegistry.Command(connection, null, """
                SELECT principal_id FROM devices WHERE device_id=$device
                """, ("$device", SecurityRegistry.Id(proof.DeviceId)));
            return string.Equals(check.ExecuteScalar() as string,
                SecurityRegistry.Id(proof.PrincipalId), StringComparison.Ordinal);
        });
    }

    private static bool HasAccess(SqliteConnection connection, SqliteTransaction? transaction,
        string device, string space, string? source, SecurityPermission permission)
    {
        using SqliteCommand command = SecurityRegistry.Command(connection, transaction, """
            SELECT 1
            FROM devices d
            JOIN principals p ON p.principal_id=d.principal_id AND p.disabled_utc IS NULL
            JOIN security_spaces s ON s.space_id=$space
            JOIN principals o ON o.principal_id=s.owner_id AND o.disabled_utc IS NULL
            LEFT JOIN space_grants g ON g.space_id=s.space_id AND g.principal_id=p.principal_id
            WHERE d.device_id=$device AND d.revoked_utc IS NULL
              AND (s.owner_id=p.principal_id OR
                   (g.revoked_utc IS NULL AND (g.permissions & $permission)=$permission))
              AND ($source IS NULL OR EXISTS
                  (SELECT 1 FROM source_spaces x
                   WHERE x.source_id=$source AND x.space_id=s.space_id))
            """,
            ("$device", device), ("$space", space), ("$source", source),
            ("$permission", (int)permission));
        return command.ExecuteScalar() is not null;
    }

    private static byte[] Message(Guid installation, Guid challenge, Guid device, Guid space,
        Guid? source, SecurityPermission permission, byte[] nonce, DateTimeOffset expires)
    {
        return Encoding.UTF8.GetBytes(
            $"Praxis.DeviceProof.v1\n{installation:N}\n{challenge:N}\n{device:N}\n" +
            $"{space:N}\n{(source is null ? "-" : source.Value.ToString("N"))}\n" +
            $"{(int)permission}\n{expires.ToUnixTimeSeconds()}\n{Convert.ToBase64String(nonce)}\n");
    }

    private static void ValidatePermission(SecurityPermission permission)
    {
        if (permission is not (SecurityPermission.Read or SecurityPermission.Write))
        {
            throw new ArgumentOutOfRangeException(nameof(permission));
        }
    }

    private static string Stamp(DateTimeOffset now) => now.UtcDateTime.ToString("O");
}
