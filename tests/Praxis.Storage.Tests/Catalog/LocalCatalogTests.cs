using Microsoft.Data.Sqlite;
using Praxis.Storage.Catalog;
using Praxis.Storage.Sources;
using Praxis.Storage.Tests.Support;
using Xunit;

namespace Praxis.Storage.Tests.Catalog;

public sealed class LocalCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(TestPaths.Temp,
        "praxis-storage-test-" + Guid.NewGuid().ToString("N"));

    public LocalCatalogTests()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void NewCatalogOpensInOwnerDirectoryWithVersionedConstraints()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        Assert.Equal(16, catalog.SchemaVersion);
        Assert.Equal(Path.Combine(root, "catalog.sqlite"), catalog.DatabasePath);
        using SqliteConnection reader = Connect(catalog.DatabasePath);
        Assert.Equal(16L, Scalar(reader, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(reader, "PRAGMA foreign_keys"));
        Assert.Equal(2L, Scalar(reader, "PRAGMA synchronous"));
        Assert.Equal("wal", Scalar(reader, "PRAGMA journal_mode"));
        Assert.Equal(14L, Scalar(reader, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('original_objects', 'sources', 'items', 'item_versions', 'ingest_requests', 'processing_jobs', 'text_derivatives', 'search_index_keys', 'source_collections', 'source_collection_memberships', 'enrichment_runs', 'enrichment_proposals', 'enrichment_reviews', 'source_locator_moves')"));
        Assert.Equal(1L, Scalar(reader, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='capture_request_context'"));
        Assert.Equal(1L, Scalar(reader, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='semantic_vectors'"));
        Assert.Equal("ok", Scalar(reader, "PRAGMA integrity_check"));
        Assert.Equal(0L, Scalar(reader, "SELECT COUNT(*) FROM sources"));
    }

    [Fact]
    public void TransactionRollbackAndReopenPreserveCommittedSource()
    {
        string path;
        using (LocalCatalog catalog = LocalCatalog.Open(root))
        {
            path = catalog.DatabasePath;
            using SqliteConnection connection = Connect(path);
            InsertSource(connection, "committed", "note.md");
            using (SqliteTransaction transaction = connection.BeginTransaction())
            {
                using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO sources(source_id, source_kind, locator, created_utc)
                    VALUES ('rolled-back', 'file', 'other.md', '2026-11-08T00:00:00Z')
                    """;
                command.ExecuteNonQuery();
                transaction.Rollback();
            }
            Assert.Throws<IOException>(() => LocalCatalog.Open(root));
        }

        using LocalCatalog reopened = LocalCatalog.Open(root);
        using SqliteConnection connection2 = Connect(path);
        Assert.Equal(1L, Scalar(connection2, "SELECT COUNT(*) FROM sources"));
        Assert.Equal("committed", Scalar(connection2, "SELECT source_id FROM sources"));
    }

    [Fact]
    public void SharedContentKeepsIndependentSourceIdentityAndForeignKeys()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using SqliteConnection connection = Connect(catalog.DatabasePath);
        const string digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        Execute(connection, """
            INSERT INTO original_objects VALUES
              ('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
               5, '2026-11-08T00:00:00Z')
            """);
        InsertSource(connection, "first", "a.md");
        InsertSource(connection, "second", "b.md");
        Execute(connection, "INSERT INTO items VALUES ('item-a', 'first'), ('item-b', 'second')");
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                INSERT INTO item_versions VALUES
                  ('item-a', 1, $digest, '2026-11-08T00:00:00Z'),
                  ('item-b', 1, $digest, '2026-11-08T00:00:00Z')
                """;
            command.Parameters.AddWithValue("$digest", digest);
            command.ExecuteNonQuery();
        }

        Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM original_objects"));
        Assert.Equal(2L, Scalar(connection, "SELECT COUNT(*) FROM item_versions"));
        Assert.Throws<SqliteException>(() => Execute(connection,
            "INSERT INTO items VALUES ('item-c', 'missing-source')"));
        Assert.Throws<SqliteException>(() => Execute(connection,
            "INSERT INTO ingest_requests (request_key, source_id, state, attempt, failure_code) VALUES ('key', 'missing-source', 'queued', 0, NULL)"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM ingest_requests"));
    }

    [Fact]
    public void CorruptAndUnknownCatalogsAreRejectedWithoutOverwriting()
    {
        using (LocalCatalog catalog = LocalCatalog.Open(root)) { }
        string path = Path.Combine(root, "catalog.sqlite");
        using (SqliteConnection connection = Connect(path))
        {
            Execute(connection, "PRAGMA user_version = 17");
        }

        Assert.Throws<NotSupportedException>(() => LocalCatalog.Open(root));
        using (SqliteConnection connection = Connect(path))
        {
            Assert.Equal(17L, Scalar(connection, "PRAGMA user_version"));
        }

        File.Delete(path);
        File.WriteAllBytes(path, [5, 7, 9]);
        Assert.Throws<SqliteException>(() => LocalCatalog.Open(root));
        Assert.Equal([5, 7, 9], File.ReadAllBytes(path));
    }

    [Fact]
    public void LegacyVersionOneCatalogUpgradesAndPreservesExistingHistory()
    {
        string path = Path.Combine(root, "catalog.sqlite");
        using (SqliteConnection connection = Connect(path))
        {
            using Stream resource = typeof(LocalCatalog).Assembly
                .GetManifestResourceStream("Praxis.Storage.LocalCatalog.sql")!;
            using StreamReader reader = new(resource);
            Execute(connection, reader.ReadToEnd());
            InsertSource(connection, "old-source", "source.md");
            Execute(connection, """
                INSERT INTO original_objects VALUES
                  ('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                   5, '2026-11-08T00:00:00Z');
                INSERT INTO items VALUES ('old-item', 'old-source');
                INSERT INTO item_versions VALUES
                  ('old-item', 1,
                   'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                   '2026-11-08T00:00:00Z');
                INSERT INTO ingest_requests (request_key, source_id, state, attempt, failure_code)
                    VALUES ('old-queued', 'old-source', 'queued', 0, NULL);
                """);
            Assert.Equal(1L, Scalar(connection, "PRAGMA user_version"));
        }

        using (LocalCatalog upgraded = LocalCatalog.Open(root))
        {
            Assert.Equal(16, upgraded.SchemaVersion);
            using SqliteConnection connection = Connect(upgraded.DatabasePath);
            Assert.Equal(16L, Scalar(connection, "PRAGMA user_version"));
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM sources"));
            Assert.Equal(1L, Scalar(connection, "SELECT COUNT(*) FROM item_versions"));
            Assert.Equal(1L, Scalar(connection,
                "SELECT COUNT(*) FROM ingest_requests WHERE revision IS NULL AND state='queued'"));
        }

        using LocalCatalog reopened = LocalCatalog.Open(root);
        using SqliteConnection verified = Connect(reopened.DatabasePath);
        Assert.Equal(16L, Scalar(verified, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(verified, "SELECT COUNT(*) FROM original_objects"));
        Assert.Equal(1L, Scalar(verified, "SELECT COUNT(*) FROM processing_jobs"));
        Assert.Equal(0L, Scalar(verified, "SELECT COUNT(*) FROM text_derivatives"));
    }

    [Fact]
    public async Task PriorSchemaReceiptsUpgradeWithoutManufacturingAuthorization()
    {
        Guid sourceId;
        using (LocalCatalog catalog = LocalCatalog.Open(root))
        {
            using MemoryStream bytes = new("Legacy original"u8.ToArray());
            CapturedOriginal original = await catalog.Originals.CaptureFileAsync(
                Path.Combine(TestPaths.Temp, "legacy-file.md"), bytes, 200, "legacy-key");
            sourceId = original.SourceId;
        }

        using (SqliteConnection connection = Connect(Path.Combine(root, "catalog.sqlite")))
        {
            Execute(connection, """
                DROP VIEW eligible_semantic_segments;
                DROP TRIGGER semantic_vectors_new_version;
                DROP TRIGGER semantic_vectors_source_revoked;
                DROP TABLE semantic_vectors;
                DROP TABLE capture_request_context;
                DROP INDEX ingest_requests_source_revision;
                PRAGMA user_version = 14;
                """);
        }

        using LocalCatalog upgraded = LocalCatalog.Open(root);
        Assert.Equal(16, upgraded.SchemaVersion);
        Assert.Equal("file", upgraded.Sources.ReadProvenance(sourceId)!.SourceKind);
        Assert.Null(upgraded.Sources.ReadProvenance(sourceId)!.CaptureRoute);
        LocalCaptureReceipt receipt = Assert.Single(upgraded.Sources.ListCaptureReceipts(sourceId));
        Assert.Equal("legacy-key", receipt.RequestKey);
        Assert.Null(receipt.Route);
        Assert.Null(receipt.AuthorityBasis);
        Assert.Null(receipt.RecordedUtc);
        await upgraded.Originals.VerifyAsync(upgraded.Sources.ReadProvenance(sourceId)!.ItemId);
    }

    [Fact]
    public void UnversionedForeignTablesAreNotAdopted()
    {
        string path = Path.Combine(root, "catalog.sqlite");
        using (SqliteConnection connection = Connect(path))
        {
            Execute(connection, "CREATE TABLE unrelated (id INTEGER)");
        }
        Assert.Throws<InvalidDataException>(() => LocalCatalog.Open(root));
        using SqliteConnection reopened = Connect(path);
        Assert.Equal(0L, Scalar(reopened, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(reopened,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'unrelated'"));
    }

    [Fact]
    public void FalselyVersionedCatalogWithIncompatibleColumnsIsRejected()
    {
        string path = Path.Combine(root, "catalog.sqlite");
        using (SqliteConnection connection = Connect(path))
        {
            Execute(connection, """
                CREATE TABLE original_objects (placeholder INTEGER) STRICT;
                CREATE TABLE sources (placeholder INTEGER) STRICT;
                CREATE TABLE items (placeholder INTEGER) STRICT;
                CREATE TABLE item_versions (placeholder INTEGER) STRICT;
                CREATE TABLE ingest_requests (placeholder INTEGER) STRICT;
                PRAGMA user_version = 1;
                """);
        }

        Assert.Throws<InvalidDataException>(() => LocalCatalog.Open(root));
        using SqliteConnection reopened = Connect(path);
        Assert.Equal(1L, Scalar(reopened, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(reopened,
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='sources'"));
    }

    [Fact]
    public void MissingRelativeAndSharedDirectoriesAreRejectedWithoutCreatingCatalog()
    {
        Assert.Throws<DirectoryNotFoundException>(() =>
            LocalCatalog.Open(Path.Combine(root, "not-created")));
        Assert.Throws<ArgumentException>(() => LocalCatalog.Open("relative/catalog"));

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.OtherExecute);
            Assert.Throws<UnauthorizedAccessException>(() => LocalCatalog.Open(root));
            Assert.False(File.Exists(Path.Combine(root, "catalog.sqlite")));
        }
    }

    [Fact]
    public void SymlinkedAndUnknownDataRootEntriesAreRejected()
    {
        string unrelated = Path.Combine(root, "my-notes.md");
        File.WriteAllText(unrelated, "Do not modify user files");
        Assert.Throws<InvalidDataException>(() => LocalCatalog.Open(root));
        Assert.Equal("Do not modify user files", File.ReadAllText(unrelated));
        File.Delete(unrelated);

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string other = Path.Combine(TestPaths.Temp, "praxis-other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            string link = Path.Combine(root, "objects");
            Directory.CreateSymbolicLink(link, other);
            Assert.Throws<InvalidDataException>(() => LocalCatalog.Open(root));
            Directory.Delete(link);
        }
        finally
        {
            Directory.Delete(other);
        }
    }

    private static SqliteConnection Connect(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void InsertSource(SqliteConnection connection, string id, string locator)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sources(source_id, source_kind, locator, created_utc)
            VALUES ($id, 'file', $locator, '2026-11-08T00:00:00Z')
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$locator", locator);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
