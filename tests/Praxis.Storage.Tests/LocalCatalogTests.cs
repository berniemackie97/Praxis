using Microsoft.Data.Sqlite;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class LocalCatalogTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
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
        using var catalog = LocalCatalog.Open(root);
        Assert.Equal(1, catalog.SchemaVersion);
        Assert.Equal(Path.Combine(root, "catalog.sqlite"), catalog.DatabasePath);
        using var reader = Connect(catalog.DatabasePath);
        Assert.Equal(1L, Scalar(reader, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(reader, "PRAGMA foreign_keys"));
        Assert.Equal(2L, Scalar(reader, "PRAGMA synchronous"));
        Assert.Equal("wal", Scalar(reader, "PRAGMA journal_mode"));
        Assert.Equal(5L, Scalar(reader, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table'"));
        Assert.Equal("ok", Scalar(reader, "PRAGMA integrity_check"));
        Assert.Equal(0L, Scalar(reader, "SELECT COUNT(*) FROM sources"));
    }

    [Fact]
    public void TransactionRollbackAndReopenPreserveCommittedSource()
    {
        string path;
        using (var catalog = LocalCatalog.Open(root))
        {
            path = catalog.DatabasePath;
            using var connection = Connect(path);
            InsertSource(connection, "committed", "note.md");
            using (var transaction = connection.BeginTransaction())
            {
                using var command = connection.CreateCommand();
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

        using var reopened = LocalCatalog.Open(root);
        using var connection2 = Connect(path);
        Assert.Equal(1L, Scalar(connection2, "SELECT COUNT(*) FROM sources"));
        Assert.Equal("committed", Scalar(connection2, "SELECT source_id FROM sources"));
    }

    [Fact]
    public void SharedContentKeepsIndependentSourceIdentityAndForeignKeys()
    {
        using var catalog = LocalCatalog.Open(root);
        using var connection = Connect(catalog.DatabasePath);
        const string digest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        Execute(connection, """
            INSERT INTO original_objects VALUES
              ('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
               5, '2026-11-08T00:00:00Z')
            """);
        InsertSource(connection, "first", "a.md");
        InsertSource(connection, "second", "b.md");
        Execute(connection, "INSERT INTO items VALUES ('item-a', 'first'), ('item-b', 'second')");
        using (var command = connection.CreateCommand())
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
            "INSERT INTO ingest_requests VALUES ('key', 'missing-source', 'queued', 0, NULL)"));
        Assert.Equal(0L, Scalar(connection, "SELECT COUNT(*) FROM ingest_requests"));
    }

    [Fact]
    public void CorruptAndUnknownCatalogsAreRejectedWithoutOverwriting()
    {
        using (var catalog = LocalCatalog.Open(root)) { }
        var path = Path.Combine(root, "catalog.sqlite");
        using (var connection = Connect(path))
        {
            Execute(connection, "PRAGMA user_version = 2");
        }

        Assert.Throws<NotSupportedException>(() => LocalCatalog.Open(root));
        using (var connection = Connect(path))
        {
            Assert.Equal(2L, Scalar(connection, "PRAGMA user_version"));
        }

        File.Delete(path);
        File.WriteAllBytes(path, [5, 7, 9]);
        Assert.Throws<SqliteException>(() => LocalCatalog.Open(root));
        Assert.Equal([5, 7, 9], File.ReadAllBytes(path));
    }

    [Fact]
    public void UnversionedForeignTablesAreNotAdopted()
    {
        var path = Path.Combine(root, "catalog.sqlite");
        using (var connection = Connect(path))
        {
            Execute(connection, "CREATE TABLE unrelated (id INTEGER)");
        }
        Assert.Throws<InvalidDataException>(() => LocalCatalog.Open(root));
        using var reopened = Connect(path);
        Assert.Equal(0L, Scalar(reopened, "PRAGMA user_version"));
        Assert.Equal(1L, Scalar(reopened,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'unrelated'"));
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
        var unrelated = Path.Combine(root, "my-notes.md");
        File.WriteAllText(unrelated, "Do not modify user files");
        Assert.Throws<InvalidDataException>(() => LocalCatalog.Open(root));
        Assert.Equal("Do not modify user files", File.ReadAllText(unrelated));
        File.Delete(unrelated);

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var other = Path.Combine(Path.GetTempPath(), "praxis-other-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(other);
        try
        {
            var link = Path.Combine(root, "objects");
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
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON";
        command.ExecuteNonQuery();
        return connection;
    }

    private static void InsertSource(SqliteConnection connection, string id, string locator)
    {
        using var command = connection.CreateCommand();
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
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
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
