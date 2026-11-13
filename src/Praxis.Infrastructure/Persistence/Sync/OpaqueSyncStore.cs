using Microsoft.Data.Sqlite;
using Praxis.Domain.Security;
using Praxis.Infrastructure.Persistence.Security.Registry;

namespace Praxis.Infrastructure.Persistence.Sync;

public sealed partial class OpaqueSyncStore : IDisposable
{
    private const string DatabaseName = "sync.sqlite";
    private const string LeaseName = "sync.lock";
    private const int MaxPayloadBytes = 64 * 1024;
    private const long MaximumStoredBytes = 128L * 1024 * 1024;
    private const int MaximumEvents = 10000;
    private readonly Lock gate = new();
    private readonly FileStream lease;
    private readonly SqliteConnection connection;
    private bool disposed;

    private OpaqueSyncStore(FileStream lease, SqliteConnection connection)
    {
        this.lease = lease;
        this.connection = connection;
    }

    public static OpaqueSyncStore Open(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (!Path.IsPathFullyQualified(directory))
        {
            throw new ArgumentException("The sync data directory must be absolute.", nameof(directory));
        }

        string root = Path.GetFullPath(directory);
        if (!Directory.Exists(root) ||
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The sync data directory must be a private regular directory.");
        }

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode exposed = UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite |
                UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(root) & exposed) != 0)
            {
                throw new UnauthorizedAccessException("The sync data directory must be owner-private.");
            }
        }

        HashSet<string> permitted = new(StringComparer.Ordinal)
        {
            DatabaseName, DatabaseName + "-wal", DatabaseName + "-shm", LeaseName
        };
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (!permitted.Contains(Path.GetFileName(entry)) ||
                (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0 ||
                !File.Exists(entry))
            {
                throw new InvalidDataException("The sync data directory contains an unexpected entry.");
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

        FileStream lease = new(Path.Combine(root, LeaseName), options);
        SqliteConnection? connection = null;
        try
        {
            string path = Path.Combine(root, DatabaseName);
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            connection.Open();
            Prepare(connection);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(Path.Combine(root, LeaseName),
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return new OpaqueSyncStore(lease, connection);
        }
        catch
        {
            connection?.Dispose();
            lease.Dispose();
            throw;
        }
    }

    public void Provision(Guid spaceId, Guid collectionEpoch)
    {
        string space = Id(spaceId);
        string epoch = Id(collectionEpoch);
        lock (gate)
        {
            EnsureOpen();
            using SqliteTransaction transaction = connection.BeginTransaction();
            using (SqliteCommand insert = Command(transaction, """
                INSERT INTO sync_spaces (space_id, collection_epoch)
                VALUES ($space, $epoch)
                ON CONFLICT(space_id) DO NOTHING
                """, ("$space", space), ("$epoch", epoch)))
            {
                insert.ExecuteNonQuery();
            }

            using SqliteCommand check = Command(transaction,
                "SELECT collection_epoch FROM sync_spaces WHERE space_id=$space",
                ("$space", space));
            if (check.ExecuteScalar() as string != epoch)
            {
                throw new InvalidOperationException("The synchronization space has a different epoch.");
            }

            transaction.Commit();
        }
    }

    internal OpaqueSyncReceipt Submit(AuthorizedSpaceScope scope, OpaqueSyncMutation request)
    {
        if (scope.Permission != SecurityPermission.Write ||
            request.SpaceId != scope.SpaceId ||
            request.DeviceId != scope.DeviceId ||
            request.ContentEpoch != scope.ContentEpoch)
        {
            throw new UnauthorizedAccessException("The requested private resource is not authorized.");
        }

        lock (gate)
        {
            EnsureOpen();
            return Write(scope, request);
        }
    }

    internal OpaqueSyncRotationReceipt StageRotation(AuthorizedSpaceScope scope, OpaqueSyncRotationStage request)
    {
        if (scope.Permission != SecurityPermission.Write ||
            scope.SpaceId != request.SpaceId || scope.DeviceId != request.DeviceId ||
            scope.ContentEpoch != request.ContentEpoch)
        {
            throw new UnauthorizedAccessException("The requested private resource is not authorized.");
        }

        lock (gate)
        {
            EnsureOpen();
            return WriteRotationStage(scope, request);
        }
    }

    internal OpaqueSyncRotationReceipt CommitRotation(AuthorizedSpaceScope scope, OpaqueSyncRotationCommit request)
    {
        if (scope.Permission != SecurityPermission.Write ||
            scope.SpaceId != request.SpaceId || scope.DeviceId != request.DeviceId ||
            scope.ContentEpoch != request.ContentEpoch)
        {
            throw new UnauthorizedAccessException("The requested private resource is not authorized.");
        }

        lock (gate)
        {
            EnsureOpen();
            return WriteRotationCommit(scope, request);
        }
    }

    internal OpaqueSyncSnapshotPage Snapshot(AuthorizedSpaceScope scope, OpaqueSyncSnapshotPull request)
    {
        if (scope.Permission != SecurityPermission.Read ||
            request.SpaceId != scope.SpaceId ||
            request.DeviceId != scope.DeviceId ||
            request.ContentEpoch != scope.ContentEpoch)
        {
            throw new UnauthorizedAccessException("The requested private resource is not authorized.");
        }

        lock (gate)
        {
            EnsureOpen();
            return ReadSnapshot(request);
        }
    }

    internal OpaqueSyncPage Pull(AuthorizedSpaceScope scope, OpaqueSyncPull request)
    {
        if (scope.Permission != SecurityPermission.Read ||
            request.SpaceId != scope.SpaceId ||
            request.DeviceId != scope.DeviceId ||
            request.ContentEpoch != scope.ContentEpoch)
        {
            throw new UnauthorizedAccessException("The requested private resource is not authorized.");
        }

        lock (gate)
        {
            EnsureOpen();
            return Read(request);
        }
    }

    private SqliteCommand Command(SqliteTransaction transaction, string sql,
        params (string Name, object? Value)[] arguments)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object? value) in arguments)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static string Id(Guid value) =>
        value == Guid.Empty
            ? throw new ArgumentException("A nonempty identity is required.")
            : value.ToString("N");

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (!disposed)
            {
                disposed = true;
                connection.Dispose();
                lease.Dispose();
            }
        }
    }
}
