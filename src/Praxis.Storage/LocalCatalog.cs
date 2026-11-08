using Microsoft.Data.Sqlite;

namespace Praxis.Storage;

public sealed class LocalCatalog : IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private static readonly string[] Tables =
        ["original_objects", "sources", "items", "item_versions", "ingest_requests"];

    private readonly FileStream lockFile;
    private readonly SqliteConnection connection;

    public string DatabasePath { get; }
    public int SchemaVersion => CurrentSchemaVersion;

    private LocalCatalog(string databasePath, FileStream lockFile, SqliteConnection connection)
    {
        DatabasePath = databasePath;
        this.lockFile = lockFile;
        this.connection = connection;
    }

    public static LocalCatalog Open(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (!Path.IsPathFullyQualified(dataDirectory))
        {
            throw new ArgumentException("The catalog data directory must be absolute.", nameof(dataDirectory));
        }

        var root = Path.GetFullPath(dataDirectory);
        ValidateRoot(root);
        var lockPath = Path.Combine(root, "catalog.lock");
        var fileLock = new FileStream(lockPath, FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        SqliteConnection? database = null;
        try
        {
            var databasePath = Path.Combine(root, "catalog.sqlite");
            database = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            database.Open();
            Execute(database, "PRAGMA foreign_keys = ON");
            Execute(database, "PRAGMA busy_timeout = 5000");
            VerifyIntegrity(database);

            var version = Convert.ToInt32(Scalar(database, "PRAGMA user_version"));
            if (version != 0 && version != CurrentSchemaVersion)
            {
                throw new NotSupportedException("The catalog schema version is not supported.");
            }

            if (version == 0)
            {
                if (Convert.ToInt64(Scalar(database, """
                    SELECT COUNT(*) FROM sqlite_master
                    WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
                    """)) != 0)
                {
                    throw new InvalidDataException("An unversioned database already contains user tables.");
                }

                using var transaction = database.BeginTransaction();
                using var migration = Command(database, ReadSchema());
                migration.Transaction = transaction;
                migration.ExecuteNonQuery();
                transaction.Commit();
            }

            VerifySchema(database);
            if (!string.Equals(Scalar(database, "PRAGMA journal_mode = WAL") as string,
                "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException("The data filesystem cannot use SQLite WAL.");
            }

            Execute(database, "PRAGMA synchronous = FULL");
            if (Convert.ToInt32(Scalar(database, "PRAGMA synchronous")) != 2 ||
                Convert.ToInt32(Scalar(database, "PRAGMA foreign_keys")) != 1)
            {
                throw new InvalidDataException("The SQLite durability or foreign key configuration is invalid.");
            }

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(databasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return new LocalCatalog(databasePath, fileLock, database);
        }
        catch
        {
            database?.Dispose();
            fileLock.Dispose();
            throw;
        }
    }

    private static void ValidateRoot(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The catalog directory cannot be a symbolic link.");
        }

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode publicAccess = UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite |
                UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(root) & publicAccess) != 0)
            {
                throw new UnauthorizedAccessException("The catalog directory must be private to its owner.");
            }
        }

        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "catalog.sqlite", "catalog.sqlite-wal", "catalog.sqlite-shm", "catalog.lock",
            "objects", "staging", "derived"
        };
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var name = Path.GetFileName(entry);
            if (!allowed.Contains(name) || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The catalog directory contains an unexpected or linked entry.");
            }

            if (name is "objects" or "staging" or "derived")
            {
                if (!Directory.Exists(entry))
                {
                    throw new InvalidDataException("A reserved catalog directory name is occupied by a file.");
                }
            }
            else if (!File.Exists(entry))
            {
                throw new InvalidDataException("A reserved catalog file name is occupied by a directory.");
            }
        }
    }

    private static void VerifyIntegrity(SqliteConnection database)
    {
        if (!string.Equals(Scalar(database, "PRAGMA integrity_check") as string,
                "ok", StringComparison.Ordinal))
        {
            throw new InvalidDataException("The catalog failed SQLite integrity verification.");
        }

        using var badReference = Command(database, "PRAGMA foreign_key_check").ExecuteReader();
        if (badReference.Read())
        {
            throw new InvalidDataException("The catalog contains invalid foreign key references.");
        }
    }

    private static void VerifySchema(SqliteConnection database)
    {
        foreach (var table in Tables)
        {
            using var command = Command(database,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name");
            command.Parameters.AddWithValue("$name", table);
            if ((long)command.ExecuteScalar()! != 1)
            {
                throw new InvalidDataException("The catalog schema is incomplete.");
            }
        }
    }

    private static string ReadSchema()
    {
        using var stream = typeof(LocalCatalog).Assembly
            .GetManifestResourceStream("Praxis.Storage.LocalCatalog.sql")
            ?? throw new InvalidOperationException("The catalog migration resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = Command(connection, sql);
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = Command(connection, sql);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    public void Dispose()
    {
        connection.Dispose();
        lockFile.Dispose();
    }
}
