using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Praxis.Infrastructure.Security;

internal static class ClientTrustSchema
{
    internal static void Prepare(SqliteConnection connection, Guid installation,
        Guid ownerDevice, ReadOnlySpan<byte> ownerKey)
    {
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=ON";
            pragma.ExecuteNonQuery();
            pragma.CommandText = "PRAGMA busy_timeout=5000";
            pragma.ExecuteNonQuery();
        }

        int version;
        using (SqliteCommand check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA user_version";
            version = Convert.ToInt32(check.ExecuteScalar());
        }

        if (version is < 0 or > 2)
        {
            throw new NotSupportedException("The client trust schema version is not supported.");
        }

        if (version == 0)
        {
            using SqliteCommand tables = connection.CreateCommand();
            tables.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            if (Convert.ToInt64(tables.ExecuteScalar()) != 0)
            {
                throw new InvalidDataException("The unversioned trust database is not empty.");
            }

            using SqliteTransaction tx = connection.BeginTransaction();
            using SqliteCommand create = connection.CreateCommand();
            create.Transaction = tx;
            create.CommandText = """
                CREATE TABLE owner_identity (
                    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                    installation_id TEXT NOT NULL,
                    owner_device_id TEXT NOT NULL,
                    public_key BLOB NOT NULL
                ) STRICT;
                CREATE TABLE device_pins (
                    space_id TEXT NOT NULL,
                    device_id TEXT NOT NULL,
                    principal_id TEXT NOT NULL,
                    public_key BLOB NOT NULL,
                    approval_id TEXT NOT NULL UNIQUE,
                    revoked_utc TEXT,
                    PRIMARY KEY(space_id, device_id)
                ) STRICT;
                CREATE TABLE trusted_epochs (
                    space_id TEXT PRIMARY KEY,
                    latest_epoch INTEGER NOT NULL CHECK (latest_epoch >= 1)
                ) STRICT;
                CREATE TABLE recovery_authority (
                    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                    public_key BLOB NOT NULL,
                    version INTEGER NOT NULL CHECK (version >= 1)
                ) STRICT;
                """;
            create.ExecuteNonQuery();
            using SqliteCommand bootstrap = SecurityRegistry.Command(connection, tx, """
                INSERT INTO owner_identity (singleton, installation_id, owner_device_id, public_key)
                VALUES (1, $installation, $owner, $key)
                """,
                ("$installation", SecurityRegistry.Id(installation)),
                ("$owner", SecurityRegistry.Id(ownerDevice)), ("$key", ownerKey.ToArray()));
            bootstrap.ExecuteNonQuery();
            using SqliteCommand setVersion = connection.CreateCommand();
            setVersion.Transaction = tx;
            setVersion.CommandText = "PRAGMA user_version=2";
            setVersion.ExecuteNonQuery();
            tx.Commit();
        }

        if (version == 1)
        {
            using SqliteTransaction tx = connection.BeginTransaction();
            using SqliteCommand migrate = connection.CreateCommand();
            migrate.Transaction = tx;
            migrate.CommandText = """
                CREATE TABLE trusted_epochs (
                    space_id TEXT PRIMARY KEY,
                    latest_epoch INTEGER NOT NULL CHECK (latest_epoch >= 1)
                ) STRICT;
                CREATE TABLE recovery_authority (
                    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                    public_key BLOB NOT NULL,
                    version INTEGER NOT NULL CHECK (version >= 1)
                ) STRICT;
                PRAGMA user_version=2;
                """;
            migrate.ExecuteNonQuery();
            tx.Commit();
        }

        using (SqliteCommand verify = connection.CreateCommand())
        {
            verify.CommandText = "SELECT installation_id, owner_device_id, public_key FROM owner_identity WHERE singleton=1";
            using SqliteDataReader reader = verify.ExecuteReader();
            if (!reader.Read() ||
                !string.Equals(reader.GetString(0), SecurityRegistry.Id(installation), StringComparison.Ordinal) ||
                !string.Equals(reader.GetString(1), SecurityRegistry.Id(ownerDevice), StringComparison.Ordinal) ||
                !CryptographicOperations.FixedTimeEquals((byte[])reader.GetValue(2), ownerKey))
            {
                throw new UnauthorizedAccessException("The client trust store belongs to another owner identity.");
            }
        }

        using (SqliteCommand integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check";
            if (!string.Equals(integrity.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
            {
                throw new InvalidDataException("The client trust database failed integrity checks.");
            }
        }

        using (SqliteCommand journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=WAL";
            if (!string.Equals(journal.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("The client trust database requires WAL.");
            }

            journal.CommandText = "PRAGMA synchronous=FULL";
            journal.ExecuteNonQuery();
        }
    }


}
