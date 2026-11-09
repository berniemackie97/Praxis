using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class LocalTextSearchTests : IDisposable
{
    private readonly string workspace = Path.Combine(
        Path.GetTempPath(), "praxis-search-" + Guid.NewGuid().ToString("N"));
    private readonly string dataRoot;

    public LocalTextSearchTests()
    {
        dataRoot = Path.Combine(workspace, "data");
        Directory.CreateDirectory(dataRoot);
        if (!OperatingSystem.IsWindows())
        {
            var privateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute;
            File.SetUnixFileMode(workspace, privateDirectory);
            File.SetUnixFileMode(dataRoot, privateDirectory);
        }
    }

    [Fact]
    public async Task SearchReturnsRankedBoundedSnippetsAndVerifiedSourceIdentity()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var first = await Capture(catalog, "journal.md",
            "# Journal\nThe midnight violet garden is blooming.", "journal");
        await Capture(catalog, "unrelated.md",
            "# Dinner\nThe kitchen is quiet.", "unrelated");
        var worker = new LocalTextProcessor(catalog);
        Assert.True(await worker.ProcessNextAsync());
        Assert.True(await worker.ProcessNextAsync());

        var hit = Assert.Single(catalog.SearchText("violet garden"));
        Assert.Equal(first.ItemId, hit.ItemId);
        Assert.Equal(first.SourceId, hit.SourceId);
        Assert.Equal(first.Revision, hit.Revision);
        Assert.Equal(first.Sha256, hit.OriginalSha256);
        Assert.Equal("journal.md", hit.Title);
        Assert.EndsWith("journal.md", hit.SourceLocator, StringComparison.Ordinal);
        Assert.Contains("⟦violet⟧", hit.Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.True(hit.Snippet.Length <= 480);
        Assert.Empty(catalog.SearchText("absent"));
        Assert.Empty(catalog.SearchText("kitchen violet"));

        Assert.Equal(2, catalog.RebuildTextSearchIndex());
        Assert.Equal(first.ItemId, Assert.Single(catalog.SearchText("violet")).ItemId);
    }

    [Fact]
    public async Task NewRevisionRetractsOldHitsBeforeItsProcessingCompletes()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var initial = await Capture(catalog, "versioned.txt", "violet first", "one");
        var worker = new LocalTextProcessor(catalog);
        Assert.True(await worker.ProcessNextAsync());
        Assert.Single(catalog.SearchText("violet"));

        var second = await Capture(catalog, "versioned.txt", "amber second", "two");
        Assert.Equal(initial.ItemId, second.ItemId);
        Assert.Equal(2, second.Revision);
        Assert.Empty(catalog.SearchText("violet"));
        Assert.Empty(catalog.SearchText("amber"));

        Assert.True(await worker.ProcessNextAsync());
        Assert.Empty(catalog.SearchText("violet"));
        var hit = Assert.Single(catalog.SearchText("amber"));
        Assert.Equal(2, hit.Revision);
        Assert.Equal(second.Sha256, hit.OriginalSha256);
        Assert.Equal("violet first", catalog.ReadTextDerivative(initial.ItemId, 1)?.Content);
        Assert.Equal(1, catalog.RebuildTextSearchIndex());
        Assert.Single(catalog.SearchText("amber"));
    }

    [Fact]
    public async Task SourceRevocationRemovesOnlyThatSourcesSearchResults()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var first = await Capture(catalog, "first.txt", "shared violet content", "one");
        var second = await Capture(catalog, "second.txt", "shared violet content", "two");
        var worker = new LocalTextProcessor(catalog);
        Assert.True(await worker.ProcessNextAsync());
        Assert.True(await worker.ProcessNextAsync());
        Assert.Equal(2, catalog.SearchText("violet").Count);

        Assert.True(catalog.RevokeSource(first.SourceId));
        var hit = Assert.Single(catalog.SearchText("violet"));
        Assert.Equal(second.SourceId, hit.SourceId);
        Assert.Equal(1, catalog.RebuildTextSearchIndex());
        Assert.Equal(second.SourceId, Assert.Single(catalog.SearchText("violet")).SourceId);
        await catalog.VerifyOriginalAsync(first.ItemId);
        await catalog.VerifyOriginalAsync(second.ItemId);
    }

    [Fact]
    public async Task DeletedDerivedIndexContentsRebuildDeterministically()
    {
        CapturedOriginal captured;
        using (var catalog = LocalCatalog.Open(dataRoot))
        {
            captured = await Capture(catalog, "rebuild.txt", "violet survives", "one");
            Assert.True(await new LocalTextProcessor(catalog).ProcessNextAsync());
            Assert.Single(catalog.SearchText("violet"));
        }

        using (var database = OpenDatabase())
        {
            using var erase = database.CreateCommand();
            erase.CommandText = "DELETE FROM text_search";
            erase.ExecuteNonQuery();
        }

        using var reopened = LocalCatalog.Open(dataRoot);
        Assert.Empty(reopened.SearchText("violet"));
        Assert.Equal(1, reopened.RebuildTextSearchIndex());
        var found = Assert.Single(reopened.SearchText("violet"));
        Assert.Equal(captured.ItemId, found.ItemId);
        Assert.Equal(1, reopened.RebuildTextSearchIndex());
        Assert.Equal(captured.ItemId, Assert.Single(reopened.SearchText("violet")).ItemId);
    }

    [Fact]
    public async Task SearchAndRebuildRejectUnboundedInputWithoutChangingIndex()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        await Capture(catalog, "first.txt", "violet bloom", "one");
        await Capture(catalog, "second.txt", "violet flower", "two");
        var worker = new LocalTextProcessor(catalog);
        Assert.True(await worker.ProcessNextAsync());
        Assert.True(await worker.ProcessNextAsync());

        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.SearchText("violet", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.SearchText("violet", 101));
        Assert.Throws<ArgumentException>(() => catalog.SearchText(new string('a', 257)));
        Assert.Throws<ArgumentException>(() => catalog.SearchText("&&&"));
        Assert.Throws<ArgumentException>(() => catalog.SearchText("violet\0"));
        Assert.Throws<ArgumentException>(() =>
            catalog.SearchText(string.Join(" ", Enumerable.Repeat("violet", 17))));
        Assert.Throws<ArgumentException>(() => catalog.SearchText(new string('z', 65)));

        Assert.Throws<InvalidOperationException>(() => catalog.RebuildTextSearchIndex(1));
        Assert.Equal(2, catalog.SearchText("violet").Count);
        Assert.Single(catalog.SearchText("violet", 1));
        Assert.Equal(2, catalog.RebuildTextSearchIndex(2));
        Assert.Equal(2, catalog.SearchText("violet").Count);
    }

    [Fact]
    public async Task BackupAndRestorePreserveSearchAndRevocationBoundary()
    {
        var backup = Path.Combine(workspace, "backup");
        var destination = Path.Combine(workspace, "restored");
        CapturedOriginal retained;
        using (var catalog = LocalCatalog.Open(dataRoot))
        {
            retained = await Capture(catalog, "retained.md", "# violet diary", "retained");
            var revoked = await Capture(catalog, "revoked.md", "# violet secret", "revoked");
            var worker = new LocalTextProcessor(catalog);
            Assert.True(await worker.ProcessNextAsync());
            Assert.True(await worker.ProcessNextAsync());
            Assert.True(catalog.RevokeSource(revoked.SourceId));
            await catalog.BackupAsync(backup, 30_000_000);
        }

        await LocalCatalog.RestoreAsync(backup, destination, 30_000_000);
        using var recovered = LocalCatalog.Open(destination);
        Assert.Equal(5, recovered.SchemaVersion);
        Assert.Equal(retained.ItemId, Assert.Single(recovered.SearchText("violet")).ItemId);
        Assert.Equal(1, recovered.RebuildTextSearchIndex());
        Assert.Equal(retained.SourceId, Assert.Single(recovered.SearchText("violet")).SourceId);
        await recovered.VerifyOriginalAsync(retained.ItemId);
    }

    [Fact]
    public void SchemaFourCatalogBackfillsOnlyCurrentActiveCompletedText()
    {
        using (var db = OpenDatabase())
        {
            foreach (var name in new[]
                { "LocalCatalog.sql", "LocalCatalog.V2.sql", "LocalCatalog.V3.sql", "LocalCatalog.V4.sql" })
            {
                using var resource = typeof(LocalCatalog).Assembly
                    .GetManifestResourceStream("Praxis.Storage." + name)!;
                using var reader = new StreamReader(resource);
                using var command = db.CreateCommand();
                command.CommandText = reader.ReadToEnd();
                command.ExecuteNonQuery();
            }

            var source = Guid.NewGuid().ToString("D");
            var item = Guid.NewGuid().ToString("D");
            var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("violet")));
            using var insert = db.CreateCommand();
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

        using var upgraded = LocalCatalog.Open(dataRoot);
        Assert.Equal(5, upgraded.SchemaVersion);
        var hit = Assert.Single(upgraded.SearchText("violet"));
        Assert.Equal("old.txt", hit.Title);
        Assert.Equal(1, upgraded.RebuildTextSearchIndex());
    }

    private async Task<CapturedOriginal> Capture(
        LocalCatalog catalog, string name, string body, string requestKey)
    {
        using var data = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return await catalog.CaptureFileAsync(
            Path.Combine(workspace, name), data, data.Length, requestKey);
    }

    private SqliteConnection OpenDatabase()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
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
