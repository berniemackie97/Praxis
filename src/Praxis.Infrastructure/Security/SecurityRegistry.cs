using Microsoft.Data.Sqlite;

namespace Praxis.Infrastructure.Security;

/// <summary>
/// Dedicated host-side identity state. It is never the owner-local content catalog and
/// does not expose an HTTP interface. Its operator surface must not be given to a
/// remote request handler.
/// </summary>
public sealed class SecurityRegistry : IDisposable
{
    private const string DatabaseName = "identity.sqlite";
    private const string LockName = "identity.lock";
    private readonly Lock gate = new();
    private readonly FileStream lease;
    private readonly SqliteConnection connection;
    private readonly TimeProvider clock;

    public Guid InstallationId { get; }
    public SecurityOperator Operator { get; }
    public DeviceProofVerifier DeviceProofs { get; }
    public SpaceKeyDistribution SpaceKeys { get; }
    public RecoveryEnrollment Recovery { get; }
    public DeviceCertificateIdentity Certificates { get; }
    public DeviceCertificateIssuer CertificateIssuer { get; }
    public ApprovedDeviceEnrollment Enrollment { get; }
    public AuthenticatedDataAccess ProtectedData { get; }

    private SecurityRegistry(FileStream lease, SqliteConnection connection, TimeProvider clock)
    {
        this.lease = lease;
        this.connection = connection;
        this.clock = clock;
        using SqliteCommand id = connection.CreateCommand();
        id.CommandText = "SELECT installation_id FROM installation WHERE singleton=1";
        InstallationId = Guid.Parse((string?)id.ExecuteScalar() ??
            throw new InvalidDataException("The security registry installation identity is missing."));
        Operator = new SecurityOperator(this);
        DeviceProofs = new DeviceProofVerifier(this);
        SpaceKeys = new SpaceKeyDistribution(this);
        Recovery = new RecoveryEnrollment(this);
        Certificates = new DeviceCertificateIdentity(this);
        CertificateIssuer = new DeviceCertificateIssuer(this);
        Enrollment = new ApprovedDeviceEnrollment(this);
        ProtectedData = new AuthenticatedDataAccess(this);
    }

    public static SecurityRegistry Open(string dataDirectory, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (!Path.IsPathFullyQualified(dataDirectory))
        {
            throw new ArgumentException("The registry directory must be absolute.", nameof(dataDirectory));
        }

        string root = Path.GetFullPath(dataDirectory);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The registry directory cannot be a symbolic link.");
        }

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode exposed = UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite |
                UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(root) & exposed) != 0)
            {
                throw new UnauthorizedAccessException("The security registry directory must be owner-private.");
            }
        }

        HashSet<string> permitted = new(StringComparer.Ordinal)
        {
            DatabaseName, DatabaseName + "-wal", DatabaseName + "-shm", LockName
        };
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            if (!permitted.Contains(Path.GetFileName(entry)) ||
                (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0 ||
                !File.Exists(entry))
            {
                throw new InvalidDataException("The security registry directory contains an unexpected entry.");
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

        FileStream lease = new(Path.Combine(root, LockName), options);
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
            SecurityRegistrySchema.Prepare(connection);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(Path.Combine(root, LockName), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return new SecurityRegistry(lease, connection, timeProvider ?? TimeProvider.System);
        }
        catch
        {
            connection?.Dispose();
            lease.Dispose();
            throw;
        }
    }

    internal DateTimeOffset UtcNow => clock.GetUtcNow();

    internal T Execute<T>(Func<SqliteConnection, T> work)
    {
        lock (gate)
        {
            return work(connection);
        }
    }

    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction,
        string sql, params (string Name, object? Value)[] arguments)
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

    internal static string Id(Guid value)
    {
        if (value == Guid.Empty)
        {
            throw new ArgumentException("A nonempty identity is required.");
        }

        return value.ToString("N");
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
