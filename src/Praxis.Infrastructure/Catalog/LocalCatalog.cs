using Microsoft.Data.Sqlite;
using Praxis.Infrastructure.Collections;
using Praxis.Infrastructure.Enrichment;
using Praxis.Infrastructure.Knowledge;
using Praxis.Infrastructure.Library;
using Praxis.Infrastructure.Media;
using Praxis.Infrastructure.Models;
using Praxis.Infrastructure.Originals;
using Praxis.Infrastructure.Processing;
using Praxis.Infrastructure.Recovery;
using Praxis.Infrastructure.Search;
using Praxis.Infrastructure.Sources;
using Praxis.Infrastructure.Verification;

namespace Praxis.Infrastructure.Catalog;

/// <summary>
/// Owner-local catalog session. Owns the exclusive directory lock and database connection,
/// and composes the responsibility-specific stores that operate on them.
/// </summary>
public sealed class LocalCatalog : IDisposable
{
    private readonly FileStream lockFile;
    private readonly CatalogDatabase database;

    public string DatabasePath { get; }
    public string DataDirectory { get; }
    public int SchemaVersion => CatalogSchema.CurrentVersion;

    public LocalSources Sources { get; }
    public LocalOriginals Originals { get; }
    public LocalLibrary Library { get; }
    public LocalProcessingQueue Processing { get; }
    public LocalTextDerivatives Text { get; }
    public LocalTextSegments Segments { get; }
    public LocalTextSearch Search { get; }
    public LocalSemanticIndex Embeddings { get; }
    public LocalHybridSearch Hybrid { get; }
    public LocalProposals Proposals { get; }
    public LocalKnowledge Knowledge { get; }
    public LocalVerification Verification { get; }
    public LocalCollections Collections { get; }
    public LocalMediaEvidence Media { get; }
    public LocalCatalogRecovery Recovery { get; }
    public LocalModelProfiles Models { get; }

    private LocalCatalog(string dataDirectory, FileStream lockFile, SqliteConnection connection)
    {
        DataDirectory = dataDirectory;
        DatabasePath = Path.Combine(dataDirectory, CatalogDirectory.DatabaseFile);
        this.lockFile = lockFile;
        database = new CatalogDatabase(connection);
        OriginalFileStore objects = new(dataDirectory);

        Sources = new LocalSources(database);
        Processing = new LocalProcessingQueue(database);
        Originals = new LocalOriginals(database, objects, dataDirectory);
        Library = new LocalLibrary(database);
        Text = new LocalTextDerivatives(database);
        Segments = new LocalTextSegments(database);
        Search = new LocalTextSearch(database);
        Embeddings = new LocalSemanticIndex(database);
        Hybrid = new LocalHybridSearch(Search, Embeddings);
        Proposals = new LocalProposals(database);
        Knowledge = new LocalKnowledge(database);
        Verification = new LocalVerification(database);
        Collections = new LocalCollections(database);
        Media = new LocalMediaEvidence(Sources, Search, Text, objects);
        Recovery = new LocalCatalogRecovery(database, objects, dataDirectory);
        Models = new LocalModelProfiles(database);
    }

    public static LocalCatalog Open(string dataDirectory) => Open(dataDirectory, SqliteOpenMode.ReadWriteCreate);

    internal static LocalCatalog OpenReadOnly(string dataDirectory) => Open(dataDirectory, SqliteOpenMode.ReadOnly);

    private static LocalCatalog Open(string dataDirectory, SqliteOpenMode mode)
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
        try
        {
            connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Mode = mode,
                Pooling = false
            }.ToString());
            connection.Open();
            if (mode == SqliteOpenMode.ReadOnly)
            {
                return new LocalCatalog(root, fileLock, connection);
            }

            CatalogSchema.Prepare(connection);
            if (OperatingSystem.IsWindows())
            {
                return new LocalCatalog(root, fileLock, connection);
            }

            File.SetUnixFileMode(databasePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.SetUnixFileMode(lockPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            return new LocalCatalog(root, fileLock, connection);
        }
        catch
        {
            connection?.Dispose();
            fileLock.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        database.Dispose();
        lockFile.Dispose();
    }
}
