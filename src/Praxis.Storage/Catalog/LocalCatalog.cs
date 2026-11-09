using Microsoft.Data.Sqlite;

namespace Praxis.Storage;

public sealed partial class LocalCatalog : IDisposable
{
    private readonly FileStream lockFile;
    private readonly SqliteConnection connection;

    public string DatabasePath { get; }
    public int SchemaVersion => CatalogSchema.CurrentVersion;

    private LocalCatalog(string databasePath, FileStream lockFile, SqliteConnection connection)
    {
        DatabasePath = databasePath;
        this.lockFile = lockFile;
        this.connection = connection;
    }

    public static LocalCatalog Open(string dataDirectory) =>
        Open(dataDirectory, SqliteOpenMode.ReadWriteCreate);

    private static LocalCatalog Open(string dataDirectory, SqliteOpenMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (!Path.IsPathFullyQualified(dataDirectory))
        {
            throw new ArgumentException("The catalog data directory must be absolute.", nameof(dataDirectory));
        }

        var root = Path.GetFullPath(dataDirectory);
        ValidateRoot(root);
        var databasePath = Path.Combine(root, "catalog.sqlite");
        if (mode == SqliteOpenMode.ReadOnly && !File.Exists(databasePath))
        {
            throw new FileNotFoundException("The source catalog does not exist.", databasePath);
        }

        var lockPath = Path.Combine(root, "catalog.lock");
        var lockOptions = new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
        {
            lockOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        var fileLock = new FileStream(lockPath, lockOptions);
        SqliteConnection? database = null;
        try
        {
            database = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = mode,
                Pooling = false
            }.ToString());
            database.Open();
            if (mode != SqliteOpenMode.ReadOnly)
            {
                CatalogSchema.Prepare(database);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(databasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
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

    public void Dispose()
    {
        connection.Dispose();
        lockFile.Dispose();
    }
}
