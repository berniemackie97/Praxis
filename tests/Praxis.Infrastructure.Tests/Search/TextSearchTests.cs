using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Praxis.Domain.Originals;
using Praxis.Domain.Search;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Persistence.Recovery;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Search;

public sealed class TextSearchTests : IDisposable
{
    private readonly string workspace = Path.Combine(
        TestPaths.Temp, "praxis-search-" + Guid.NewGuid().ToString("N"));
    private readonly string dataRoot;

    public TextSearchTests()
    {
        dataRoot = Path.Combine(workspace, "data");
        Directory.CreateDirectory(dataRoot);
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode privateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                            UnixFileMode.UserExecute;
            File.SetUnixFileMode(workspace, privateDirectory);
            File.SetUnixFileMode(dataRoot, privateDirectory);
        }
    }

    [Fact]
    public async Task SearchReturnsRankedBoundedSnippetsAndVerifiedSourceIdentity()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        CapturedOriginal first = await Capture(catalog, "journal.md",
            "# Journal\nThe midnight violet garden is blooming.", "journal");
        await Capture(catalog, "unrelated.md",
            "# Dinner\nThe kitchen is quiet.", "unrelated");
        ProcessingDriver worker = ProcessingDriver.Text(catalog);
        Assert.True(await worker.ProcessNextAsync());
        Assert.True(await worker.ProcessNextAsync());

        SearchHit hit = Assert.Single(catalog.Search.Query("violet garden"));
        Assert.Equal(first.ItemId, hit.ItemId);
        Assert.Equal(first.SourceId, hit.SourceId);
        Assert.Equal(first.Revision, hit.Revision);
        Assert.Equal(first.Sha256, hit.OriginalSha256);
        Assert.Equal("journal.md", hit.Title);
        Assert.EndsWith("journal.md", hit.SourceLocator, StringComparison.Ordinal);
        Assert.Contains("⟦violet⟧", hit.Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.True(hit.Snippet.Length <= 480);
        Assert.Contains("⟦journal⟧", Assert.Single(catalog.Search.Query("journal")).Snippet,
            StringComparison.OrdinalIgnoreCase);
        Assert.Empty(catalog.Search.Query("absent"));
        Assert.Empty(catalog.Search.Query("kitchen violet"));

        Assert.Equal(2, catalog.Search.Rebuild());
        Assert.Equal(first.ItemId, Assert.Single(catalog.Search.Query("violet")).ItemId);
    }

    [Fact]
    public async Task CanonicalEvidenceFenceRejectsForgedMetadataAndEarlierSourceRevisions()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        CapturedOriginal original = await Capture(catalog, "evidence.md",
            "violet primary evidence", "initial");
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        SearchHit current = Assert.Single(catalog.Search.Query("violet"));
        Assert.Equal(current, Assert.Single(catalog.Search.RetainCurrent([current])));

        Assert.Empty(catalog.Search.RetainCurrent([current with { SourceId = Guid.NewGuid() }]));
        Assert.Empty(catalog.Search.RetainCurrent([current with { Revision = 42 }]));
        Assert.Empty(catalog.Search.RetainCurrent(
            [current with { OriginalSha256 = new string('0', 64) }]));
        Assert.Empty(catalog.Search.RetainCurrent(
            [current with { SourceLocator = current.SourceLocator + ".stale" }]));
        Assert.Empty(catalog.Search.RetainCurrent([]));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            catalog.Search.RetainCurrent(Enumerable.Repeat(current, 101).ToArray()));

        CapturedOriginal revised = await Capture(catalog, "evidence.md",
            "amber second revision", "revised");
        Assert.Equal(original.ItemId, revised.ItemId);
        Assert.Empty(catalog.Search.RetainCurrent([current]));
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        SearchHit latest = Assert.Single(catalog.Search.Query("amber"));
        Assert.Equal(2, latest.Revision);
        Assert.Equal(latest, Assert.Single(catalog.Search.RetainCurrent([latest])));

        Assert.True(catalog.Sources.Revoke(original.SourceId));
        Assert.Empty(catalog.Search.RetainCurrent([latest]));
    }

    [Fact]
    public async Task UnsupportedOriginalsNeverBecomeSearchableText()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        CapturedOriginal file = await Capture(catalog, "report.pdf", "violet in an unsupported pdf", "pdf");
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        Assert.Equal("unsupported", catalog.Text.Read(file.ItemId, 1)?.Outcome);
        Assert.Empty(catalog.Search.Query("violet"));
        Assert.Equal(0, catalog.Search.Rebuild());
        Assert.Empty(catalog.Search.Query("violet"));
        await catalog.Originals.VerifyAsync(file.ItemId);
    }

    [Fact]
    public async Task NewRevisionRetractsOldHitsBeforeItsProcessingCompletes()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        CapturedOriginal initial = await Capture(catalog, "versioned.txt", "violet first", "one");
        ProcessingDriver worker = ProcessingDriver.Text(catalog);
        Assert.True(await worker.ProcessNextAsync());
        Assert.Single(catalog.Search.Query("violet"));

        CapturedOriginal second = await Capture(catalog, "versioned.txt", "amber second", "two");
        Assert.Equal(initial.ItemId, second.ItemId);
        Assert.Equal(2, second.Revision);
        Assert.Empty(catalog.Search.Query("violet"));
        Assert.Empty(catalog.Search.Query("amber"));

        Assert.True(await worker.ProcessNextAsync());
        Assert.Empty(catalog.Search.Query("violet"));
        SearchHit hit = Assert.Single(catalog.Search.Query("amber"));
        Assert.Equal(2, hit.Revision);
        Assert.Equal(second.Sha256, hit.OriginalSha256);
        Assert.Equal("violet first", catalog.Text.Read(initial.ItemId, 1)?.Content);
        Assert.Equal(1, catalog.Search.Rebuild());
        Assert.Single(catalog.Search.Query("amber"));
    }

    [Fact]
    public async Task SourceRevocationRemovesOnlyThatSourcesSearchResults()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        CapturedOriginal first = await Capture(catalog, "first.txt", "shared violet content", "one");
        CapturedOriginal second = await Capture(catalog, "second.txt", "shared violet content", "two");
        ProcessingDriver worker = ProcessingDriver.Text(catalog);
        Assert.True(await worker.ProcessNextAsync());
        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(2, catalog.Search.Query("violet").Count);

        Assert.True(catalog.Sources.Revoke(first.SourceId));
        SearchHit hit = Assert.Single(catalog.Search.Query("violet"));
        Assert.Equal(second.SourceId, hit.SourceId);
        Assert.Equal(1, catalog.Search.Rebuild());
        Assert.Equal(second.SourceId, Assert.Single(catalog.Search.Query("violet")).SourceId);
        await catalog.Originals.VerifyAsync(first.ItemId);
        await catalog.Originals.VerifyAsync(second.ItemId);
    }

    [Fact]
    public async Task DeletedDerivedIndexContentsRebuildDeterministically()
    {
        CapturedOriginal captured;
        using (SqliteCatalog catalog = SqliteCatalog.Open(dataRoot))
        {
            captured = await Capture(catalog, "rebuild.txt", "violet survives", "one");
            Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
            Assert.Single(catalog.Search.Query("violet"));
        }

        using (SqliteConnection database = OpenDatabase())
        {
            using SqliteCommand erase = database.CreateCommand();
            erase.CommandText = "DELETE FROM text_search";
            erase.ExecuteNonQuery();
        }

        using SqliteCatalog reopened = SqliteCatalog.Open(dataRoot);
        Assert.Empty(reopened.Search.Query("violet"));
        Assert.Equal(1, reopened.Search.Rebuild());
        SearchHit found = Assert.Single(reopened.Search.Query("violet"));
        Assert.Equal(captured.ItemId, found.ItemId);
        Assert.Equal(1, reopened.Search.Rebuild());
        Assert.Equal(captured.ItemId, Assert.Single(reopened.Search.Query("violet")).ItemId);
    }

    [Fact]
    public async Task SearchAndRebuildRejectUnboundedInputWithoutChangingIndex()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        await Capture(catalog, "first.txt", "violet bloom", "one");
        await Capture(catalog, "second.txt", "violet flower", "two");
        ProcessingDriver worker = ProcessingDriver.Text(catalog);
        Assert.True(await worker.ProcessNextAsync());
        Assert.True(await worker.ProcessNextAsync());

        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.Search.Query("violet", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.Search.Query("violet", 101));
        Assert.Throws<ArgumentException>(() => catalog.Search.Query(new string('a', 257)));
        Assert.Throws<ArgumentException>(() => catalog.Search.Query("&&&"));
        Assert.Throws<ArgumentException>(() => catalog.Search.Query("violet\0"));
        Assert.Throws<ArgumentException>(() =>
            catalog.Search.Query(string.Join(" ", Enumerable.Repeat("violet", 17))));
        Assert.Throws<ArgumentException>(() => catalog.Search.Query(new string('z', 65)));

        Assert.Throws<InvalidOperationException>(() => catalog.Search.Rebuild(1));
        Assert.Throws<InvalidOperationException>(() => catalog.Search.Rebuild(
            maxDocuments: 2, maxTextBytes: 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.Search.Rebuild(
            maxTextBytes: 0));
        Assert.Empty(catalog.Search.Query("violet OR nonexistent"));
        Assert.Equal(2, catalog.Search.Query("violet").Count);
        Assert.Single(catalog.Search.Query("violet", 1));
        Assert.Equal(2, catalog.Search.Rebuild(2));
        Assert.Equal(2, catalog.Search.Query("violet").Count);
    }

    [Fact]
    public async Task BackupAndRestorePreserveSearchAndRevocationBoundary()
    {
        string backup = Path.Combine(workspace, "backup");
        string destination = Path.Combine(workspace, "restored");
        CapturedOriginal retained;
        using (SqliteCatalog catalog = SqliteCatalog.Open(dataRoot))
        {
            retained = await Capture(catalog, "retained.md", "# violet diary", "retained");
            CapturedOriginal revoked = await Capture(catalog, "revoked.md", "# violet secret", "revoked");
            ProcessingDriver worker = ProcessingDriver.Text(catalog);
            Assert.True(await worker.ProcessNextAsync());
            Assert.True(await worker.ProcessNextAsync());
            Assert.True(catalog.Sources.Revoke(revoked.SourceId));
            await catalog.Recovery.BackupAsync(backup, 30_000_000);
        }

        await CatalogSnapshots.RestoreAsync(backup, destination, 30_000_000);
        using SqliteCatalog recovered = SqliteCatalog.Open(destination);
        Assert.Equal(17, recovered.SchemaVersion);
        Assert.Equal(retained.ItemId, Assert.Single(recovered.Search.Query("violet")).ItemId);
        Assert.Equal(1, recovered.Search.Rebuild());
        Assert.Equal(retained.SourceId, Assert.Single(recovered.Search.Query("violet")).SourceId);
        await recovered.Originals.VerifyAsync(retained.ItemId);
    }

    [Fact]
    public void SchemaFourCatalogBackfillsOnlyCurrentActiveCompletedText()
    {
        using (SqliteConnection db = OpenDatabase())
        {
            foreach (string name in new[]
                { "Catalog.V1.sql", "Catalog.V2.sql", "Catalog.V3.sql", "Catalog.V4.sql" })
            {
                using Stream resource = typeof(SqliteCatalog).Assembly
                    .GetManifestResourceStream("Praxis.Infrastructure." + name)!;
                using StreamReader reader = new(resource);
                using SqliteCommand command = db.CreateCommand();
                command.CommandText = reader.ReadToEnd();
                command.ExecuteNonQuery();
            }

            string source = Guid.NewGuid().ToString("D");
            string item = Guid.NewGuid().ToString("D");
            string digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("violet")));
            using SqliteCommand insert = db.CreateCommand();
            insert.CommandText = """
                INSERT INTO original_objects VALUES ($digest, 6, '2026-11-08T00:00:00Z');
                INSERT INTO sources (source_id, source_kind, locator, created_utc)
                    VALUES ($source, 'file', $locator, '2026-11-08T00:00:00Z');
                INSERT INTO items VALUES ($item, $source);
                INSERT INTO item_versions VALUES ($item, 1, $digest, '2026-11-08T00:00:00Z');
                INSERT INTO processing_jobs
                    (item_id, revision, processor_key, state, attempt, total_attempts,
                     created_utc, updated_utc)
                    VALUES ($item, 1, 'source-text-v1', 'completed', 1, 1,
                            '2026-11-08T00:00:00Z', '2026-11-08T00:00:00Z');
                INSERT INTO text_derivatives
                    (item_id, revision, processor_key, source_digest,
                     outcome, content, created_utc)
                    VALUES ($item, 1, 'source-text-v1', $digest,
                            'completed', 'violet', '2026-11-08T00:00:00Z');
                """;
            insert.Parameters.AddWithValue("$digest", digest);
            insert.Parameters.AddWithValue("$source", source);
            insert.Parameters.AddWithValue("$item", item);
            insert.Parameters.AddWithValue("$locator", Path.Combine(workspace, "old.txt"));
            insert.ExecuteNonQuery();
        }

        using SqliteCatalog upgraded = SqliteCatalog.Open(dataRoot);
        Assert.Equal(17, upgraded.SchemaVersion);
        SearchHit hit = Assert.Single(upgraded.Search.Query("violet"));
        Assert.Equal("old.txt", hit.Title);
        Assert.Equal(1, upgraded.Search.Rebuild());
    }

    private async Task<CapturedOriginal> Capture(
        SqliteCatalog catalog, string name, string body, string requestKey)
    {
        using MemoryStream data = new(Encoding.UTF8.GetBytes(body));
        return await catalog.Originals.CaptureFileAsync(
            Path.Combine(workspace, name), data, data.Length, requestKey);
    }

    private SqliteConnection OpenDatabase()
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataRoot, "catalog.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, true);
        }
    }
}
