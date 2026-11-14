using Microsoft.Data.Sqlite;
using Praxis.Infrastructure.FileSystem;
using Praxis.Infrastructure.Persistence.Agents;
using Praxis.Infrastructure.Persistence.Collections;
using Praxis.Infrastructure.Persistence.Enrichment;
using Praxis.Infrastructure.Persistence.Finance;
using Praxis.Infrastructure.Persistence.Intake;
using Praxis.Infrastructure.Persistence.Knowledge;
using Praxis.Infrastructure.Persistence.Library;
using Praxis.Infrastructure.Persistence.Links;
using Praxis.Infrastructure.Persistence.Models;
using Praxis.Infrastructure.Persistence.Originals;
using Praxis.Infrastructure.Persistence.Policy;
using Praxis.Infrastructure.Persistence.Processing;
using Praxis.Infrastructure.Persistence.Projects;
using Praxis.Infrastructure.Persistence.Recovery;
using Praxis.Infrastructure.Persistence.Search;
using Praxis.Infrastructure.Persistence.Sources;
using Praxis.Infrastructure.Persistence.Text;
using Praxis.Infrastructure.Persistence.Understanding;
using Praxis.Infrastructure.Persistence.Verification;

namespace Praxis.Infrastructure.Persistence;

/// <summary>
/// Owner-local catalog session. Owns the exclusive directory lock and database connection,
/// and composes the responsibility-specific stores that operate on them.
/// </summary>
public sealed class SqliteCatalog : IDisposable
{
    private readonly FileStream lockFile;
    private readonly CatalogDatabase database;

    public string DatabasePath { get; }
    public string DataDirectory { get; }
    public int SchemaVersion => CatalogSchema.CurrentVersion;

    public SqliteSourceRepository Sources { get; }
    public SqliteOriginalStore Originals { get; }
    public SqliteLibraryReader Library { get; }
    public SqliteProcessingQueue Processing { get; }
    public SqliteIntakeIncidentJournal IntakeIncidents { get; }
    public SqliteTextDerivativeRepository Text { get; }
    public SqliteTextSegmentRepository Segments { get; }
    public SqliteTextSearchIndex Search { get; }
    public SqliteSemanticVectorStore Embeddings { get; }
    public SqliteProposalRepository Proposals { get; }
    public SqliteKnowledgeRepository Knowledge { get; }
    public SqliteGraphReader Graph { get; }
    public SqliteVerificationRepository Verification { get; }
    public SqliteCollectionRepository Collections { get; }
    public CatalogRecovery Recovery { get; }
    public SqliteModelProfileRepository Models { get; }
    public SqliteFinancialLedgerRepository Finance { get; }
    public SqlitePolicyRepository Policy { get; }
    public SqliteAgentRepository Agents { get; }
    public SqliteUnderstandingRepository Understanding { get; }

    public SqliteLinkRepository Links { get; }

    public SqliteProjectRepository Projects { get; }

    private SqliteCatalog(string dataDirectory, FileStream lockFile, SqliteConnection connection, SqliteConnection? reader, TimeProvider clock)
    {
        DataDirectory = dataDirectory;
        DatabasePath = Path.Combine(dataDirectory, CatalogDirectory.DatabaseFile);
        this.lockFile = lockFile;
        database = new CatalogDatabase(connection, reader);
        OriginalFileStore objects = new(dataDirectory);

        Sources = new SqliteSourceRepository(database, clock);
        Processing = new SqliteProcessingQueue(database, clock);
        IntakeIncidents = new SqliteIntakeIncidentJournal(database, clock);
        Originals = new SqliteOriginalStore(database, objects, dataDirectory, clock);
        Library = new SqliteLibraryReader(database);
        Text = new SqliteTextDerivativeRepository(database, clock);
        Segments = new SqliteTextSegmentRepository(database, clock);
        Search = new SqliteTextSearchIndex(database);
        Embeddings = new SqliteSemanticVectorStore(database, clock);
        Proposals = new SqliteProposalRepository(database, clock);
        Knowledge = new SqliteKnowledgeRepository(database, clock);
        Graph = new SqliteGraphReader(database);
        Verification = new SqliteVerificationRepository(database, clock);
        Collections = new SqliteCollectionRepository(database, clock);
        Recovery = new CatalogRecovery(database, objects, dataDirectory);
        Models = new SqliteModelProfileRepository(database, clock);
        Finance = new SqliteFinancialLedgerRepository(database, clock);
        Policy = new SqlitePolicyRepository(database, clock);
        Agents = new SqliteAgentRepository(database, clock);
        Understanding = new SqliteUnderstandingRepository(database, clock);
        Links = new SqliteLinkRepository(database, clock);
        Projects = new SqliteProjectRepository(database);
    }

    public static SqliteCatalog Open(string dataDirectory, TimeProvider? clock = null) =>
        Open(dataDirectory, SqliteOpenMode.ReadWriteCreate, clock ?? TimeProvider.System);

    internal static SqliteCatalog OpenReadOnly(string dataDirectory) => Open(dataDirectory, SqliteOpenMode.ReadOnly, TimeProvider.System);

    private static SqliteCatalog Open(string dataDirectory, SqliteOpenMode mode, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (!Path.IsPathFullyQualified(dataDirectory))
        {
            throw new ArgumentException("The catalog data directory must be absolute.", nameof(dataDirectory));
        }

        string root = Path.GetFullPath(dataDirectory);
        CatalogDirectory.Validate(root);
        string databasePath = Path.Combine(root, CatalogDirectory.DatabaseFile);
        if (mode == SqliteOpenMode.ReadOnly && !File.Exists(databasePath))
        {
            throw new FileNotFoundException("The source catalog does not exist.", databasePath);
        }

        string lockPath = Path.Combine(root, CatalogDirectory.LockFile);
        FileStreamOptions lockOptions = new()
        {
            Mode = FileMode.OpenOrCreate,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None
        };
        if (!OperatingSystem.IsWindows())
        {
            lockOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        FileStream fileLock = new(lockPath, lockOptions);
        SqliteConnection? connection = null;
        SqliteConnection? reader = null;
        try
        {
            connection = Connect(databasePath, mode);
            if (mode == SqliteOpenMode.ReadOnly)
            {
                return new SqliteCatalog(root, fileLock, connection, null, clock);
            }

            CatalogSchema.Prepare(connection);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(databasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            reader = Connect(databasePath, SqliteOpenMode.ReadOnly);
            using (SqliteCommand readOnly = reader.CreateCommand("PRAGMA query_only = ON; PRAGMA busy_timeout = 5000"))
            {
                readOnly.ExecuteNonQuery();
            }

            return new SqliteCatalog(root, fileLock, connection, reader, clock);
        }
        catch
        {
            reader?.Dispose();
            connection?.Dispose();
            fileLock.Dispose();
            throw;
        }
    }

    private static SqliteConnection Connect(string databasePath, SqliteOpenMode mode)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = mode,
            Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        database.Dispose();
        lockFile.Dispose();
    }
}
