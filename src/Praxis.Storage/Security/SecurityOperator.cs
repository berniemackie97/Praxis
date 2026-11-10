using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Praxis.Storage.Security;

[Flags]
public enum SecurityPermission
{
    Read = 1,
    Write = 2
}

public enum SecurityProcessingClass
{
    ClientHeld,
    TrustedHost
}

/// <summary>
/// Owner/operator-only provisioning. Do not register this class as a request-scoped
/// service or expose its methods over the unauthenticated network.
/// </summary>
public sealed class SecurityOperator
{
    private readonly SecurityRegistry registry;

    internal SecurityOperator(SecurityRegistry registry) => this.registry = registry;

    public Guid CreatePrincipal()
    {
        Guid id = Guid.NewGuid();
        registry.Execute(connection =>
        {
            using SqliteCommand command = SecurityRegistry.Command(connection, null,
                "INSERT INTO principals (principal_id, created_utc) VALUES ($id, $now)",
                ("$id", SecurityRegistry.Id(id)), ("$now", Now()));
            command.ExecuteNonQuery();
            return 0;
        });
        return id;
    }

    public Guid EnrollDevice(Guid principalId, ReadOnlySpan<byte> subjectPublicKeyInfo) =>
        EnrollDevice(principalId, subjectPublicKeyInfo, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

    public Guid EnrollDevice(Guid principalId, ReadOnlySpan<byte> subjectPublicKeyInfo,
        ReadOnlySpan<byte> wrappingPublicKey, ReadOnlySpan<byte> wrappingBindingSignature)
    {
        if (subjectPublicKeyInfo.Length is < 80 or > 128)
        {
            throw new ArgumentException("The device public key is invalid.", nameof(subjectPublicKeyInfo));
        }

        using (ECDsa verifier = ECDsa.Create())
        {
            verifier.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out int read);
            if (read != subjectPublicKeyInfo.Length || verifier.KeySize != 256 ||
                verifier.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            {
                throw new ArgumentException("Device identity requires a P-256 signing key.", nameof(subjectPublicKeyInfo));
            }
        }

        if ((!wrappingPublicKey.IsEmpty || !wrappingBindingSignature.IsEmpty) &&
            !DeviceWrappingKey.Verify(subjectPublicKeyInfo, wrappingPublicKey, wrappingBindingSignature))
        {
            throw new ArgumentException("The recipient key is not signed by its enrolled identity.", nameof(wrappingPublicKey));
        }

        byte[] key = subjectPublicKeyInfo.ToArray();
        byte[]? recipientKey = wrappingPublicKey.IsEmpty ? null : wrappingPublicKey.ToArray();
        byte[]? binding = wrappingBindingSignature.IsEmpty ? null : wrappingBindingSignature.ToArray();
        Guid deviceId = Guid.NewGuid();
        bool inserted = registry.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = SecurityRegistry.Command(connection, transaction, """
                INSERT INTO devices (device_id, principal_id, public_key, wrapping_public_key, wrapping_signature, enrolled_utc)
                SELECT $device, principal_id, $key, $wrapping, $binding, $now FROM principals
                WHERE principal_id = $principal AND disabled_utc IS NULL
                """,
                ("$device", SecurityRegistry.Id(deviceId)),
                ("$principal", SecurityRegistry.Id(principalId)),
                ("$key", key), ("$wrapping", recipientKey),
                ("$binding", binding),
                ("$now", Now()));
            bool created = command.ExecuteNonQuery() == 1;
            if (created)
            {
                RequireRekeyForPrincipal(connection, transaction, principalId);
                transaction.Commit();
            }

            return created;
        });
        if (!inserted)
        {
            throw new UnauthorizedAccessException("The principal is not eligible for enrollment.");
        }

        return deviceId;
    }

    public void RegisterWrappingKey(Guid deviceId, ReadOnlySpan<byte> wrappingPublicKey,
        ReadOnlySpan<byte> wrappingBindingSignature)
    {
        byte[] key = wrappingPublicKey.ToArray();
        byte[] signature = wrappingBindingSignature.ToArray();
        registry.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            Guid principalId;
            byte[] signingKey;
            using (SqliteCommand read = SecurityRegistry.Command(connection, transaction, """
                SELECT principal_id, public_key FROM devices
                WHERE device_id=$device AND revoked_utc IS NULL AND wrapping_public_key IS NULL
                """, ("$device", SecurityRegistry.Id(deviceId))))
            using (SqliteDataReader reader = read.ExecuteReader())
            {
                if (!reader.Read())
                {
                    throw new InvalidOperationException("The active device has no available wrapping-key slot.");
                }

                principalId = Guid.Parse(reader.GetString(0));
                signingKey = (byte[])reader.GetValue(1);
            }

            if (!DeviceWrappingKey.Verify(signingKey, key, signature))
            {
                throw new ArgumentException("The wrapping key binding is invalid.", nameof(wrappingPublicKey));
            }

            using (SqliteCommand update = SecurityRegistry.Command(connection, transaction, """
                UPDATE devices SET wrapping_public_key=$wrapping, wrapping_signature=$signature
                WHERE device_id=$device AND revoked_utc IS NULL AND wrapping_public_key IS NULL
                """, ("$device", SecurityRegistry.Id(deviceId)), ("$wrapping", key), ("$signature", signature)))
            {
                if (update.ExecuteNonQuery() != 1)
                {
                    throw new InvalidOperationException("The device wrapping key has already been set.");
                }
            }

            RequireRekeyForPrincipal(connection, transaction, principalId);
            transaction.Commit();
            return 0;
        });
    }

    public Guid CreateSpace(Guid ownerId, SecurityProcessingClass processingClass)
    {
        string mode = processingClass switch
        {
            SecurityProcessingClass.ClientHeld => "client-held",
            SecurityProcessingClass.TrustedHost => "trusted-host",
            _ => throw new ArgumentOutOfRangeException(nameof(processingClass))
        };
        Guid spaceId = Guid.NewGuid();
        bool inserted = registry.Execute(connection =>
        {
            using SqliteCommand command = SecurityRegistry.Command(connection, null, """
                INSERT INTO security_spaces (space_id, owner_id, processing_class, created_utc)
                SELECT $space, principal_id, $class, $now FROM principals
                WHERE principal_id=$owner AND disabled_utc IS NULL
                """,
                ("$space", SecurityRegistry.Id(spaceId)),
                ("$owner", SecurityRegistry.Id(ownerId)),
                ("$class", mode),
                ("$now", Now()));
            return command.ExecuteNonQuery() == 1;
        });
        if (!inserted)
        {
            throw new UnauthorizedAccessException("The space owner is not eligible.");
        }

        return spaceId;
    }

    public void Grant(Guid ownerId, Guid spaceId, Guid memberId, SecurityPermission permissions)
    {
        int bits = (int)permissions;
        if (bits is < 1 or > 3)
        {
            throw new ArgumentOutOfRangeException(nameof(permissions));
        }

        if (ownerId == memberId)
        {
            throw new ArgumentException("The owner already has the space's permissions.");
        }

        bool inserted = registry.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = SecurityRegistry.Command(connection, transaction, """
                INSERT INTO space_grants (space_id, principal_id, permissions, revoked_utc)
                SELECT s.space_id, p.principal_id, $permissions, NULL
                FROM security_spaces s
                JOIN principals p ON p.principal_id=$member AND p.disabled_utc IS NULL
                WHERE s.space_id=$space AND s.owner_id=$owner
                  AND EXISTS (SELECT 1 FROM principals o
                              WHERE o.principal_id=s.owner_id AND o.disabled_utc IS NULL)
                ON CONFLICT(space_id, principal_id)
                DO UPDATE SET permissions=excluded.permissions, revoked_utc=NULL,
                                  grant_version=space_grants.grant_version + 1
                """,
                ("$space", SecurityRegistry.Id(spaceId)),
                ("$member", SecurityRegistry.Id(memberId)),
                ("$owner", SecurityRegistry.Id(ownerId)),
                ("$permissions", bits));
            bool granted = command.ExecuteNonQuery() == 1;
            if (granted)
            {
                using SqliteCommand rotate = SecurityRegistry.Command(connection, transaction,
                    "UPDATE security_spaces SET rekey_required=1 WHERE space_id=$space",
                    ("$space", SecurityRegistry.Id(spaceId)));
                rotate.ExecuteNonQuery();
                transaction.Commit();
            }

            return granted;
        });
        if (!inserted)
        {
            throw new UnauthorizedAccessException("The space grant is not authorized.");
        }
    }

    public bool RevokeGrant(Guid ownerId, Guid spaceId, Guid memberId)
    {
        return registry.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = SecurityRegistry.Command(connection, transaction, """
                UPDATE space_grants SET revoked_utc=$now, grant_version=grant_version + 1
                WHERE space_id=$space AND principal_id=$member AND revoked_utc IS NULL
                  AND EXISTS (SELECT 1 FROM security_spaces s
                              WHERE s.space_id=$space AND s.owner_id=$owner)
                """,
                ("$space", SecurityRegistry.Id(spaceId)),
                ("$member", SecurityRegistry.Id(memberId)),
                ("$owner", SecurityRegistry.Id(ownerId)),
                ("$now", Now()));
            bool revoked = command.ExecuteNonQuery() == 1;
            if (revoked)
            {
                using SqliteCommand rotate = SecurityRegistry.Command(connection, transaction,
                    "UPDATE security_spaces SET rekey_required=1 WHERE space_id=$space",
                    ("$space", SecurityRegistry.Id(spaceId)));
                rotate.ExecuteNonQuery();
            }

            transaction.Commit();
            return revoked;
        });
    }

    public bool RevokeDevice(Guid deviceId)
    {
        return registry.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = SecurityRegistry.Command(connection, transaction, """
                UPDATE devices SET revoked_utc=$now
                WHERE device_id=$device AND revoked_utc IS NULL
                """,
                ("$device", SecurityRegistry.Id(deviceId)), ("$now", Now()));
            bool revoked = command.ExecuteNonQuery() == 1;
            if (revoked)
            {
                using SqliteCommand purge = SecurityRegistry.Command(connection, transaction,
                    "DELETE FROM proof_challenges WHERE device_id=$device",
                    ("$device", SecurityRegistry.Id(deviceId)));
                purge.ExecuteNonQuery();
                using SqliteCommand principal = SecurityRegistry.Command(connection, transaction,
                    "SELECT principal_id FROM devices WHERE device_id=$device",
                    ("$device", SecurityRegistry.Id(deviceId)));
                Guid owner = Guid.Parse((string)principal.ExecuteScalar()!);
                RequireRekeyForPrincipal(connection, transaction, owner);
            }

            transaction.Commit();
            return revoked;
        });
    }

    public bool DisablePrincipal(Guid principalId)
    {
        return registry.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = SecurityRegistry.Command(connection, transaction, """
                UPDATE principals SET disabled_utc=$now
                WHERE principal_id=$principal AND disabled_utc IS NULL
                """,
                ("$principal", SecurityRegistry.Id(principalId)), ("$now", Now()));
            bool disabled = command.ExecuteNonQuery() == 1;
            if (disabled)
            {
                using SqliteCommand purge = SecurityRegistry.Command(connection, transaction, """
                    DELETE FROM proof_challenges WHERE device_id IN
                        (SELECT device_id FROM devices WHERE principal_id=$principal)
                    """, ("$principal", SecurityRegistry.Id(principalId)));
                purge.ExecuteNonQuery();
                RequireRekeyForPrincipal(connection, transaction, principalId);
            }

            transaction.Commit();
            return disabled;
        });
    }

    public void AssignSource(Guid ownerId, Guid spaceId, Guid sourceId)
    {
        string source = SecurityRegistry.Id(sourceId);
        registry.Execute(connection =>
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand command = SecurityRegistry.Command(connection, transaction, """
                INSERT INTO source_spaces (source_id, space_id)
                SELECT $source, space_id FROM security_spaces
                WHERE space_id=$space AND owner_id=$owner
                ON CONFLICT(source_id) DO NOTHING
                """,
                ("$source", source),
                ("$space", SecurityRegistry.Id(spaceId)),
                ("$owner", SecurityRegistry.Id(ownerId)));
            if (command.ExecuteNonQuery() != 1)
            {
                using SqliteCommand check = SecurityRegistry.Command(connection, transaction, """
                    SELECT space_id FROM source_spaces WHERE source_id=$source
                    """, ("$source", source));
                if (!string.Equals(check.ExecuteScalar() as string, SecurityRegistry.Id(spaceId),
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("A source cannot belong to two security spaces.");
                }

                using SqliteCommand owner = SecurityRegistry.Command(connection, transaction, """
                    SELECT 1 FROM security_spaces WHERE space_id=$space AND owner_id=$owner
                    """, ("$space", SecurityRegistry.Id(spaceId)), ("$owner", SecurityRegistry.Id(ownerId)));
                if (owner.ExecuteScalar() is null)
                {
                    throw new UnauthorizedAccessException("Only the owner may assign a source.");
                }
            }

            transaction.Commit();
            return 0;
        });
    }

    internal static void RequireRekeyForPrincipal(SqliteConnection connection,
        SqliteTransaction transaction, Guid principalId)
    {
        using SqliteCommand rotate = SecurityRegistry.Command(connection, transaction, """
            UPDATE security_spaces SET rekey_required=1
            WHERE owner_id=$principal OR space_id IN
                (SELECT space_id FROM space_grants
                 WHERE principal_id=$principal AND revoked_utc IS NULL
                   AND (permissions & 1)=1)
            """, ("$principal", SecurityRegistry.Id(principalId)));
        rotate.ExecuteNonQuery();
    }

    private string Now() => registry.UtcNow.UtcDateTime.ToString("O");
}
