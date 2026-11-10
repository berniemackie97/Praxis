using Microsoft.Data.Sqlite;

namespace Praxis.Storage.Security;

internal static class SecurityRegistrySchema
{
    internal const int Version = 4;

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

        if (version <= 1)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand upgrade = connection.CreateCommand();
            upgrade.Transaction = transaction;
            upgrade.CommandText = """
                ALTER TABLE devices ADD COLUMN wrapping_public_key BLOB;
                ALTER TABLE devices ADD COLUMN wrapping_signature BLOB;
                ALTER TABLE security_spaces ADD COLUMN rekey_required INTEGER NOT NULL DEFAULT 1 CHECK (rekey_required IN (0, 1));
                CREATE TABLE space_key_epochs (
                    space_id TEXT NOT NULL REFERENCES security_spaces(space_id),
                    epoch INTEGER NOT NULL CHECK (epoch >= 1),
                    commitment BLOB NOT NULL CHECK (length(commitment) = 32),
                    signer_device_id TEXT NOT NULL REFERENCES devices(device_id),
                    recovery_nonce BLOB NOT NULL CHECK (length(recovery_nonce) = 12),
                    recovery_ciphertext BLOB NOT NULL CHECK (length(recovery_ciphertext) = 48),
                    recovery_signature BLOB NOT NULL CHECK (length(recovery_signature) = 64),
                    created_utc TEXT NOT NULL,
                    PRIMARY KEY (space_id, epoch)
                ) STRICT;
                CREATE TABLE device_key_envelopes (
                    space_id TEXT NOT NULL,
                    epoch INTEGER NOT NULL,
                    device_id TEXT NOT NULL REFERENCES devices(device_id),
                    wrapped_key BLOB NOT NULL CHECK (length(wrapped_key) = 384),
                    signature BLOB NOT NULL CHECK (length(signature) = 64),
                    PRIMARY KEY (space_id, epoch, device_id),
                    FOREIGN KEY (space_id, epoch) REFERENCES space_key_epochs(space_id, epoch)
                ) STRICT;
                PRAGMA user_version=2;
                """;
            upgrade.ExecuteNonQuery();
            transaction.Commit();
        }

        if (version <= 2)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand upgrade = connection.CreateCommand();
            upgrade.Transaction = transaction;
            upgrade.CommandText = """
                CREATE TABLE principal_recovery (
                    principal_id TEXT PRIMARY KEY REFERENCES principals(principal_id),
                    public_key BLOB NOT NULL CHECK (length(public_key) BETWEEN 80 AND 128),
                    version INTEGER NOT NULL DEFAULT 1 CHECK (version >= 1),
                    registered_utc TEXT NOT NULL
                ) STRICT;
                CREATE TABLE recovery_enrollments (
                    enrollment_id TEXT PRIMARY KEY,
                    principal_id TEXT NOT NULL REFERENCES principals(principal_id),
                    signing_key BLOB NOT NULL CHECK (length(signing_key) BETWEEN 80 AND 128),
                    wrapping_key BLOB NOT NULL CHECK (length(wrapping_key) BETWEEN 350 AND 600),
                    wrapping_signature BLOB NOT NULL CHECK (length(wrapping_signature) = 64),
                    recovery_version INTEGER NOT NULL CHECK (recovery_version >= 1),
                    nonce BLOB NOT NULL CHECK (length(nonce) = 32),
                    expires_utc TEXT NOT NULL,
                    consumed INTEGER NOT NULL DEFAULT 0 CHECK (consumed IN (0, 1))
                ) STRICT;
                CREATE INDEX recovery_enrollments_principal
                    ON recovery_enrollments(principal_id, expires_utc);
                PRAGMA user_version=3;
                """;
            upgrade.ExecuteNonQuery();
            transaction.Commit();
        }

        if (version <= 3)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand upgrade = connection.CreateCommand();
            upgrade.Transaction = transaction;
            upgrade.CommandText = """
                CREATE TABLE device_approvals (
                    device_id TEXT PRIMARY KEY REFERENCES devices(device_id),
                    space_id TEXT NOT NULL REFERENCES security_spaces(space_id),
                    approved_by_device_id TEXT NOT NULL REFERENCES devices(device_id),
                    approved_principal_id TEXT NOT NULL REFERENCES principals(principal_id),
                    challenge_id TEXT NOT NULL UNIQUE,
                    nonce BLOB NOT NULL CHECK (length(nonce) = 32),
                    expires_utc TEXT NOT NULL,
                    owner_signature BLOB NOT NULL CHECK (length(owner_signature) = 64),
                    applicant_signature BLOB NOT NULL CHECK (length(applicant_signature) = 64)
                ) STRICT;
                CREATE TABLE pending_device_approvals (
                    challenge_id TEXT PRIMARY KEY,
                    device_id TEXT NOT NULL UNIQUE,
                    space_id TEXT NOT NULL REFERENCES security_spaces(space_id),
                    owner_device_id TEXT NOT NULL REFERENCES devices(device_id),
                    principal_id TEXT NOT NULL REFERENCES principals(principal_id),
                    signing_public_key BLOB NOT NULL CHECK (length(signing_public_key) BETWEEN 80 AND 128),
                    wrapping_public_key BLOB NOT NULL CHECK (length(wrapping_public_key) BETWEEN 350 AND 600),
                    wrapping_signature BLOB NOT NULL CHECK (length(wrapping_signature) = 64),
                    nonce BLOB NOT NULL CHECK (length(nonce) = 32),
                    expires_utc TEXT NOT NULL,
                    consumed INTEGER NOT NULL DEFAULT 0 CHECK (consumed IN (0, 1))
                ) STRICT;
                CREATE INDEX pending_device_approvals_owner
                    ON pending_device_approvals(owner_device_id, expires_utc);
                PRAGMA user_version=4;
                """;
            upgrade.ExecuteNonQuery();
            transaction.Commit();
        }

        VerifySchema(connection);

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
    internal static void VerifySchema(SqliteConnection connection)
    {
        foreach ((string table, string columns) in new (string, string)[]
        {
            ("installation", "singleton, installation_id"),
            ("principals", "principal_id, created_utc, disabled_utc"),
            ("devices", "device_id, principal_id, public_key, enrolled_utc, revoked_utc, wrapping_public_key, wrapping_signature"),
            ("security_spaces", "space_id, owner_id, processing_class, created_utc, rekey_required"),
            ("space_grants", "space_id, principal_id, permissions, grant_version, revoked_utc"),
            ("source_spaces", "source_id, space_id"),
            ("proof_challenges", "challenge_id, device_id, space_id, source_id, permission, grant_version, nonce, expires_utc, consumed"),
            ("space_key_epochs", "space_id, epoch, commitment, signer_device_id, recovery_nonce, recovery_ciphertext, recovery_signature, created_utc"),
            ("device_key_envelopes", "space_id, epoch, device_id, wrapped_key, signature"),
            ("principal_recovery", "principal_id, public_key, version, registered_utc"),
            ("recovery_enrollments", "enrollment_id, principal_id, signing_key, wrapping_key, wrapping_signature, recovery_version, nonce, expires_utc, consumed"),
            ("device_approvals", "device_id, space_id, approved_by_device_id, approved_principal_id, challenge_id, nonce, expires_utc, owner_signature, applicant_signature"),
            ("pending_device_approvals", "challenge_id, device_id, space_id, owner_device_id, principal_id, signing_public_key, wrapping_public_key, wrapping_signature, nonce, expires_utc, consumed")
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

    }

}
