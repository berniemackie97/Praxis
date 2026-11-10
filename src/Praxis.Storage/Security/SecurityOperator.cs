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

    public Guid EnrollDevice(Guid principalId, ReadOnlySpan<byte> subjectPublicKeyInfo)
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

        byte[] key = subjectPublicKeyInfo.ToArray();
        Guid deviceId = Guid.NewGuid();
        bool inserted = registry.Execute(connection =>
        {
            using SqliteCommand command = SecurityRegistry.Command(connection, null, """
                INSERT INTO devices (device_id, principal_id, public_key, enrolled_utc)
                SELECT $device, principal_id, $key, $now FROM principals
                WHERE principal_id = $principal AND disabled_utc IS NULL
                """,
                ("$device", SecurityRegistry.Id(deviceId)),
                ("$principal", SecurityRegistry.Id(principalId)),
                ("$key", key),
                ("$now", Now()));
            return command.ExecuteNonQuery() == 1;
        });
        if (!inserted)
        {
            throw new UnauthorizedAccessException("The principal is not eligible for enrollment.");
        }

        return deviceId;
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
            using SqliteCommand command = SecurityRegistry.Command(connection, null, """
                INSERT INTO space_grants (space_id, principal_id, permissions, revoked_utc)
                SELECT s.space_id, p.principal_id, $permissions, NULL
                FROM security_spaces s
                JOIN principals p ON p.principal_id=$member AND p.disabled_utc IS NULL
                WHERE s.space_id=$space AND s.owner_id=$owner
                  AND EXISTS (SELECT 1 FROM principals o
                              WHERE o.principal_id=s.owner_id AND o.disabled_utc IS NULL)
                ON CONFLICT(space_id, principal_id)
                DO UPDATE SET permissions=excluded.permissions, revoked_utc=NULL
                """,
                ("$space", SecurityRegistry.Id(spaceId)),
                ("$member", SecurityRegistry.Id(memberId)),
                ("$owner", SecurityRegistry.Id(ownerId)),
                ("$permissions", bits));
            return command.ExecuteNonQuery() == 1;
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
            using SqliteCommand command = SecurityRegistry.Command(connection, null, """
                UPDATE space_grants SET revoked_utc=$now
                WHERE space_id=$space AND principal_id=$member AND revoked_utc IS NULL
                  AND EXISTS (SELECT 1 FROM security_spaces s
                              WHERE s.space_id=$space AND s.owner_id=$owner)
                """,
                ("$space", SecurityRegistry.Id(spaceId)),
                ("$member", SecurityRegistry.Id(memberId)),
                ("$owner", SecurityRegistry.Id(ownerId)),
                ("$now", Now()));
            return command.ExecuteNonQuery() == 1;
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

    private string Now() => registry.UtcNow.UtcDateTime.ToString("O");
}
