using Microsoft.Data.Sqlite;

namespace Praxis.Storage.Security;

internal static class SecurityRegistrySchema
{
    internal const int Version = 1;

    internal static void Prepare(SqliteConnection connection)
    {
        using (SqliteCommand options = connection.CreateCommand())
        {
            options.CommandText = "PRAGMA foreign_keys=ON";
            options.ExecuteNonQuery();
            options.CommandText = "PRAGMA busy_timeout=5000";
            options.ExecuteNonQuery();
        }

        using (SqliteCommand check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA integrity_check";
            if (check.ExecuteScalar() is not string result || result != "ok")
            {
                throw new InvalidDataException("The security registry failed integrity verification.");
            }
        }

        int version;
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version";
            version = Convert.ToInt32(command.ExecuteScalar());
        }

        if (version is < 0 or > Version)
        {
            throw new NotSupportedException("The security registry schema is not supported.");
        }

        if (version == 0)
        {
            using SqliteCommand existing = connection.CreateCommand();
            existing.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'";
            if (Convert.ToInt64(existing.ExecuteScalar()) != 0)
            {
                throw new InvalidDataException("An unversioned security registry contains tables.");
            }

            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand create = connection.CreateCommand();
            create.Transaction = transaction;
            create.CommandText = """
                CREATE TABLE installation (
                    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                    installation_id TEXT NOT NULL UNIQUE
                ) STRICT;
                CREATE TABLE principals (
                    principal_id TEXT PRIMARY KEY,
                    created_utc TEXT NOT NULL,
                    disabled_utc TEXT
                ) STRICT;
                CREATE TABLE devices (
                    device_id TEXT PRIMARY KEY,
                    principal_id TEXT NOT NULL REFERENCES principals(principal_id),
                    public_key BLOB NOT NULL UNIQUE CHECK (length(public_key) BETWEEN 80 AND 128),
                    enrolled_utc TEXT NOT NULL,
                    revoked_utc TEXT
                ) STRICT;
                CREATE TABLE security_spaces (
                    space_id TEXT PRIMARY KEY,
                    owner_id TEXT NOT NULL REFERENCES principals(principal_id),
                    processing_class TEXT NOT NULL CHECK (processing_class IN ('client-held', 'trusted-host')),
                    created_utc TEXT NOT NULL
                ) STRICT;
                CREATE TABLE space_grants (
                    space_id TEXT NOT NULL REFERENCES security_spaces(space_id),
                    principal_id TEXT NOT NULL REFERENCES principals(principal_id),
                    permissions INTEGER NOT NULL CHECK (permissions BETWEEN 1 AND 3),
                    grant_version INTEGER NOT NULL DEFAULT 1 CHECK (grant_version >= 1),
                    revoked_utc TEXT,
                    PRIMARY KEY (space_id, principal_id)
                ) STRICT;
                CREATE TABLE source_spaces (
                    source_id TEXT PRIMARY KEY,
                    space_id TEXT NOT NULL REFERENCES security_spaces(space_id)
                ) STRICT;
                CREATE TABLE proof_challenges (
                    challenge_id TEXT PRIMARY KEY,
                    device_id TEXT NOT NULL REFERENCES devices(device_id),
                    space_id TEXT NOT NULL REFERENCES security_spaces(space_id),
                    source_id TEXT,
                    permission INTEGER NOT NULL CHECK (permission IN (1, 2)),
                    grant_version INTEGER NOT NULL CHECK (grant_version >= 0),
                    nonce BLOB NOT NULL CHECK (length(nonce) = 32),
                    expires_utc TEXT NOT NULL,
                    consumed INTEGER NOT NULL DEFAULT 0 CHECK (consumed IN (0, 1))
                ) STRICT;
                CREATE INDEX proof_challenges_device ON proof_challenges(device_id, expires_utc);
                """;
            create.ExecuteNonQuery();
            using (SqliteCommand identity = connection.CreateCommand())
            {
                identity.Transaction = transaction;
                identity.CommandText = "INSERT INTO installation VALUES (1, $installation)";
                identity.Parameters.AddWithValue("$installation", Guid.NewGuid().ToString("N"));
                identity.ExecuteNonQuery();
            }

            using (SqliteCommand versionUpdate = connection.CreateCommand())
            {
                versionUpdate.Transaction = transaction;
                versionUpdate.CommandText = "PRAGMA user_version=1";
                versionUpdate.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        foreach ((string table, string columns) in new (string, string)[]
        {
            ("installation", "singleton, installation_id"),
            ("principals", "principal_id, created_utc, disabled_utc"),
            ("devices", "device_id, principal_id, public_key, enrolled_utc, revoked_utc"),
            ("security_spaces", "space_id, owner_id, processing_class, created_utc"),
            ("space_grants", "space_id, principal_id, permissions, grant_version, revoked_utc"),
            ("source_spaces", "source_id, space_id"),
            ("proof_challenges", "challenge_id, device_id, space_id, source_id, permission, grant_version, nonce, expires_utc, consumed")
        })
        {
            using SqliteCommand check = connection.CreateCommand();
            check.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$table";
            check.Parameters.AddWithValue("$table", table);
            if (check.ExecuteScalar() is not string definition ||
                !definition.TrimEnd().EndsWith("STRICT", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The security registry schema is incompatible.");
            }

            using SqliteCommand projection = connection.CreateCommand();
            projection.CommandText = $"SELECT {columns} FROM {table} LIMIT 0";
            try
            {
                using SqliteDataReader reader = projection.ExecuteReader();
            }
            catch (SqliteException exception)
            {
                throw new InvalidDataException("The security registry schema is incomplete.", exception);
            }
        }

        using (SqliteCommand keys = connection.CreateCommand())
        {
            keys.CommandText = "PRAGMA foreign_key_check";
            using SqliteDataReader invalid = keys.ExecuteReader();
            if (invalid.Read())
            {
                throw new InvalidDataException("The security registry contains invalid references.");
            }
        }

        using (SqliteCommand journal = connection.CreateCommand())
        {
            journal.CommandText = "PRAGMA journal_mode=WAL";
            if (journal.ExecuteScalar() is not string mode || !mode.Equals("wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("The security registry requires SQLite WAL.");
            }
        }

        using (SqliteCommand durability = connection.CreateCommand())
        {
            durability.CommandText = "PRAGMA synchronous=FULL";
            durability.ExecuteNonQuery();
        }
        using (SqliteCommand check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA synchronous";
            if (Convert.ToInt32(check.ExecuteScalar()) != 2)
            {
                throw new InvalidDataException("The security registry requires full commit durability.");
            }
        }
    }
}
