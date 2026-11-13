using Microsoft.Data.Sqlite;

namespace Praxis.Infrastructure.Persistence.Sync;

public sealed partial class OpaqueSyncStore
{
    private static void Prepare(SqliteConnection connection)
    {
        int version;
        using (SqliteCommand pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA foreign_keys=ON";
            pragma.ExecuteNonQuery();
            pragma.CommandText = "PRAGMA busy_timeout=5000";
            pragma.ExecuteNonQuery();
            pragma.CommandText = "PRAGMA integrity_check";
            if (pragma.ExecuteScalar() is not string integrity || integrity != "ok")
            {
                throw new InvalidDataException("The sync database failed integrity verification.");
            }

            pragma.CommandText = "PRAGMA user_version";
            version = Convert.ToInt32(pragma.ExecuteScalar());
            if (version is not (0 or 1 or 2))
            {
                throw new NotSupportedException("The sync database schema is unsupported.");
            }

            if (version == 0)
            {
                pragma.CommandText = """
                    SELECT COUNT(*) FROM sqlite_master
                    WHERE type='table' AND name NOT LIKE 'sqlite_%'
                    """;
                if (Convert.ToInt64(pragma.ExecuteScalar()) != 0)
                {
                    throw new InvalidDataException("The unversioned sync database contains tables.");
                }

                using SqliteTransaction transaction = connection.BeginTransaction();
                using SqliteCommand setup = connection.CreateCommand();
                setup.Transaction = transaction;
                setup.CommandText = """
                    CREATE TABLE sync_spaces (
                        space_id TEXT PRIMARY KEY,
                        collection_epoch TEXT NOT NULL,
                        head_sequence INTEGER NOT NULL DEFAULT 0 CHECK(head_sequence >= 0),
                        history_floor INTEGER NOT NULL DEFAULT 0 CHECK(history_floor >= 0)
                    ) STRICT;
                    CREATE TABLE sync_records (
                        record_id TEXT PRIMARY KEY,
                        space_id TEXT NOT NULL REFERENCES sync_spaces(space_id),
                        revision INTEGER NOT NULL CHECK(revision >= 1),
                        deleted INTEGER NOT NULL CHECK(deleted IN (0, 1)),
                        key_epoch INTEGER NOT NULL CHECK(key_epoch >= 1),
                        ciphertext BLOB NOT NULL
                    ) STRICT;
                    CREATE INDEX sync_records_space ON sync_records(space_id, record_id);
                    CREATE TABLE sync_events (
                        space_id TEXT NOT NULL REFERENCES sync_spaces(space_id),
                        sequence INTEGER NOT NULL CHECK(sequence >= 1),
                        record_id TEXT NOT NULL REFERENCES sync_records(record_id),
                        revision INTEGER NOT NULL CHECK(revision >= 1),
                        deleted INTEGER NOT NULL CHECK(deleted IN (0, 1)),
                        key_epoch INTEGER NOT NULL CHECK(key_epoch >= 1),
                        ciphertext BLOB NOT NULL,
                        PRIMARY KEY(space_id, sequence)
                    ) STRICT;
                    CREATE TABLE sync_receipts (
                        space_id TEXT NOT NULL REFERENCES sync_spaces(space_id),
                        device_id TEXT NOT NULL,
                        operation_id TEXT NOT NULL,
                        request_hash BLOB NOT NULL CHECK(length(request_hash) = 32),
                        sequence INTEGER NOT NULL CHECK(sequence >= 1),
                        revision INTEGER NOT NULL CHECK(revision >= 1),
                        PRIMARY KEY(space_id, device_id, operation_id)
                    ) STRICT;
                    PRAGMA user_version=1;
                    """;
                setup.ExecuteNonQuery();
                transaction.Commit();
            }
        }

        if (version <= 1)
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            using SqliteCommand upgrade = connection.CreateCommand();
            upgrade.Transaction = transaction;
            upgrade.CommandText = """
                ALTER TABLE sync_spaces ADD COLUMN content_epoch INTEGER NOT NULL
                    DEFAULT 0 CHECK(content_epoch >= 0);
                ALTER TABLE sync_spaces ADD COLUMN last_rotation_id TEXT;
                CREATE TABLE sync_rotation (
                    space_id TEXT PRIMARY KEY REFERENCES sync_spaces(space_id),
                    rotation_id TEXT NOT NULL,
                    expected_head INTEGER NOT NULL CHECK(expected_head >= 1),
                    old_epoch INTEGER NOT NULL CHECK(old_epoch >= 1),
                    new_epoch INTEGER NOT NULL CHECK(new_epoch >= 2)
                ) STRICT;
                CREATE TABLE sync_rotation_records (
                    space_id TEXT NOT NULL REFERENCES sync_rotation(space_id) ON DELETE CASCADE,
                    record_id TEXT NOT NULL,
                    revision INTEGER NOT NULL CHECK(revision >= 1),
                    deleted INTEGER NOT NULL CHECK(deleted IN (0, 1)),
                    ciphertext BLOB NOT NULL CHECK(length(ciphertext) BETWEEN 29 AND 65536),
                    PRIMARY KEY(space_id, record_id)
                ) STRICT;
                PRAGMA user_version=2;
                """;
            upgrade.ExecuteNonQuery();
            transaction.Commit();
        }

        foreach (string name in new[]
        {
            "sync_spaces", "sync_records", "sync_events", "sync_receipts",
            "sync_rotation", "sync_rotation_records"
        })
        {
            using SqliteCommand check = connection.CreateCommand();
            check.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name=$name";
            check.Parameters.AddWithValue("$name", name);
            if (check.ExecuteScalar() is not string definition ||
                !definition.TrimEnd().EndsWith("STRICT", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The sync database schema is incompatible.");
            }
        }

        using (SqliteCommand check = connection.CreateCommand())
        {
            check.CommandText = "PRAGMA foreign_key_check";
            using SqliteDataReader invalid = check.ExecuteReader();
            if (invalid.Read())
            {
                throw new InvalidDataException("The sync database contains broken references.");
            }
        }

        using SqliteCommand mode = connection.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode=WAL";
        if (mode.ExecuteScalar() is not string wal ||
            !wal.Equals("wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("The sync database requires SQLite WAL.");
        }

        mode.CommandText = "PRAGMA synchronous=FULL";
        mode.ExecuteNonQuery();
        mode.CommandText = "PRAGMA synchronous";
        if (Convert.ToInt32(mode.ExecuteScalar()) != 2)
        {
            throw new InvalidDataException("The sync database requires full commit durability.");
        }
    }
}
