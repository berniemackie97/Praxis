using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Praxis.Domain.Security;
using Praxis.Infrastructure.Persistence.Security.Registry;

namespace Praxis.Infrastructure.Persistence.Security.ClientTrust;

/// <summary>
/// Independently bootstrapped, owner-private recipient pins. Never populate it by
/// copying a host-returned recipient directory without a verified owner approval.
/// </summary>
public sealed partial class ClientDeviceTrustStore : IDisposable
{
    private readonly object gate = new();
    private readonly FileStream lease;
    private readonly SqliteConnection connection;
    private readonly Guid installationId;
    private Guid ownerDeviceId;
    private byte[] ownerPublicKey;
    private readonly TimeProvider clock;

    private ClientDeviceTrustStore(FileStream lease, SqliteConnection connection,
        Guid installation, Guid ownerDevice, byte[] key, TimeProvider clock)
    {
        this.clock = clock;
        this.lease = lease;
        this.connection = connection;
        installationId = installation;
        ownerDeviceId = ownerDevice;
        ownerPublicKey = key;
    }

    public static ClientDeviceTrustStore Open(string directory, Guid installation,
        Guid ownerDevice, ReadOnlySpan<byte> independentlyVerifiedOwnerPublicKey, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (installation == Guid.Empty || ownerDevice == Guid.Empty ||
            !Path.IsPathFullyQualified(directory) ||
            independentlyVerifiedOwnerPublicKey.Length is < 80 or > 128)
        {
            throw new ArgumentException("The trust store requires a private directory and a pinned owner identity.");
        }

        using (ECDsa verifier = ECDsa.Create())
        {
            verifier.ImportSubjectPublicKeyInfo(independentlyVerifiedOwnerPublicKey, out int length);
            if (length != independentlyVerifiedOwnerPublicKey.Length || verifier.KeySize != 256 ||
                verifier.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
            {
                throw new ArgumentException("The pinned owner identity must use P-256.");
            }
        }

        string root = Path.GetFullPath(directory);
        if (!Directory.Exists(root) ||
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The trust store must be an existing private directory.");
        }

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode exposed = UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite |
                UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(root) & exposed) != 0)
            {
                throw new UnauthorizedAccessException("The client trust directory must be owner-private.");
            }
        }

        HashSet<string> expected = new(StringComparer.Ordinal)
        {
            "trust.sqlite", "trust.sqlite-wal", "trust.sqlite-shm", "trust.lock"
        };
        foreach (string path in Directory.EnumerateFileSystemEntries(root))
        {
            if (!expected.Contains(Path.GetFileName(path)) ||
                !File.Exists(path) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The trust directory contains an unexpected entry.");
            }
        }

        FileStreamOptions options = new()
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        FileStream lease = new(Path.Combine(root, "trust.lock"), options);
        SqliteConnection? connection = null;
        try
        {
            string path = Path.Combine(root, "trust.sqlite");
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            connection.Open();
            ClientTrustSchema.Prepare(connection, installation, ownerDevice, independentlyVerifiedOwnerPublicKey);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(Path.Combine(root, "trust.lock"),
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return new ClientDeviceTrustStore(lease, connection, installation,
                ownerDevice, independentlyVerifiedOwnerPublicKey.ToArray(), timeProvider ?? TimeProvider.System);
        }
        catch
        {
            connection?.Dispose();
            lease.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Approvals have short validity. Once accepted, the signing-key pin survives
    /// expiry and process restart until the owner explicitly revokes it.
    /// </summary>
    public void Accept(ApprovedDeviceIdentity approval)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (approval.InstallationId != installationId ||
            approval.ApproverDeviceId != ownerDeviceId ||
            approval.DeviceId == ownerDeviceId ||
            approval.SpaceId == Guid.Empty ||
            approval.PrincipalId == Guid.Empty ||
            !ApprovedDeviceEnrollment.VerifyApproval(approval, ownerPublicKey, clock.GetUtcNow()))
        {
            throw new UnauthorizedAccessException("A current, independently verified owner approval is required.");
        }

        lock (gate)
        {
            using SqliteTransaction tx = connection.BeginTransaction();
            using (SqliteCommand previous = SecurityRegistry.Command(connection, tx, """
                SELECT public_key FROM device_pins WHERE device_id=$device LIMIT 1
                """, ("$device", SecurityRegistry.Id(approval.DeviceId))))
            {
                if (previous.ExecuteScalar() is byte[] existing &&
                    !CryptographicOperations.FixedTimeEquals(existing, approval.SigningPublicKey))
                {
                    throw new UnauthorizedAccessException("An approved device identity cannot change.");
                }
            }

            using SqliteCommand add = SecurityRegistry.Command(connection, tx, """
                INSERT INTO device_pins
                    (space_id, device_id, principal_id, public_key, approval_id, revoked_utc)
                VALUES ($space, $device, $principal, $key, $approval, NULL)
                ON CONFLICT(space_id, device_id) DO NOTHING
                """,
                ("$space", SecurityRegistry.Id(approval.SpaceId)),
                ("$device", SecurityRegistry.Id(approval.DeviceId)),
                ("$principal", SecurityRegistry.Id(approval.PrincipalId)),
                ("$key", approval.SigningPublicKey),
                ("$approval", SecurityRegistry.Id(approval.ChallengeId)));
            if (add.ExecuteNonQuery() != 1)
            {
                throw new InvalidOperationException("The device is already pinned or revoked.");
            }

            tx.Commit();
        }
    }

    public bool Revoke(Guid spaceId, Guid deviceId, ECDsa ownerSigner)
    {
        ArgumentNullException.ThrowIfNull(ownerSigner);
        if (deviceId == ownerDeviceId)
        {
            throw new InvalidOperationException("The trust anchor must be replaced out of band.");
        }

        if (!CryptographicOperations.FixedTimeEquals(
            ownerSigner.ExportSubjectPublicKeyInfo(), ownerPublicKey))
        {
            throw new UnauthorizedAccessException("Only the local pinned owner can revoke trusted recipients.");
        }

        DateTimeOffset now = clock.GetUtcNow();
        byte[] message = System.Text.Encoding.UTF8.GetBytes("Praxis.ClientPinRevocation.v1\n" +
            $"{installationId:N}\n{ownerDeviceId:N}\n{spaceId:N}\n{deviceId:N}\n" +
            $"{now.ToUnixTimeSeconds()}\n");
        byte[] signature;
        try
        {
            signature = ownerSigner.SignData(message, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (CryptographicException exception)
        {
            throw new UnauthorizedAccessException("Private-key possession is required for revocation.", exception);
        }

        using (ECDsa verifier = ECDsa.Create())
        {
            verifier.ImportSubjectPublicKeyInfo(ownerPublicKey, out _);
            if (!verifier.VerifyData(message, signature, HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                throw new UnauthorizedAccessException("The owner revocation signature is invalid.");
            }
        }

        lock (gate)
        {
            using SqliteCommand revoke = SecurityRegistry.Command(connection, null, """
                UPDATE device_pins SET revoked_utc=$now
                WHERE space_id=$space AND device_id=$device AND revoked_utc IS NULL
                """,
                ("$space", SecurityRegistry.Id(spaceId)),
                ("$device", SecurityRegistry.Id(deviceId)),
                ("$now", now.UtcDateTime.ToString("O")));
            return revoke.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>Highest authenticated current-key epoch seen by this client, across restarts.</summary>
    public long MinimumCurrentEpoch(Guid spaceId)
    {
        lock (gate)
        {
            using SqliteCommand read = SecurityRegistry.Command(connection, null,
                "SELECT latest_epoch FROM trusted_epochs WHERE space_id=$space",
                ("$space", SecurityRegistry.Id(spaceId)));
            return read.ExecuteScalar() is long version ? version : 1;
        }
    }

    internal void RecordVerifiedCurrentEpoch(Guid spaceId, long epoch)
    {
        if (epoch < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(epoch));
        }

        lock (gate)
        {
            using SqliteCommand write = SecurityRegistry.Command(connection, null, """
                INSERT INTO trusted_epochs (space_id, latest_epoch)
                VALUES ($space, $epoch)
                ON CONFLICT(space_id) DO UPDATE SET latest_epoch=excluded.latest_epoch
                WHERE trusted_epochs.latest_epoch <= excluded.latest_epoch
                """,
                ("$space", SecurityRegistry.Id(spaceId)), ("$epoch", epoch));
            if (write.ExecuteNonQuery() != 1)
            {
                throw new CryptographicException("A previously verified newer key epoch cannot be rolled back.");
            }
        }
    }

    internal ECDsa PinnedOwnerVerifier(Guid installation, Guid signingDeviceId)
    {
        if (installation != installationId || signingDeviceId != ownerDeviceId)
        {
            throw new UnauthorizedAccessException("The signing owner must match the independently pinned trust root.");
        }

        ECDsa verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(ownerPublicKey, out _);
        return verifier;
    }

    public IReadOnlyDictionary<Guid, byte[]> TrustedRecipients(Guid spaceId)
    {
        string space = SecurityRegistry.Id(spaceId);
        lock (gate)
        {
            Dictionary<Guid, byte[]> pinned = new() { [ownerDeviceId] = ownerPublicKey.ToArray() };
            using SqliteCommand read = SecurityRegistry.Command(connection, null, """
                SELECT device_id, public_key FROM device_pins
                WHERE space_id=$space AND revoked_utc IS NULL
                """, ("$space", space));
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                pinned.Add(Guid.Parse(reader.GetString(0)), (byte[])reader.GetValue(1));
            }

            return pinned;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            connection.Dispose();
            lease.Dispose();
        }
    }
}
