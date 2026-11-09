using Microsoft.Data.Sqlite;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class LocalCollectionsTests : IDisposable
{
    private readonly string workspace = Path.Combine(
        Path.GetTempPath(), "praxis-collections-" + Guid.NewGuid().ToString("N"));
    private readonly string dataRoot;

    public LocalCollectionsTests()
    {
        dataRoot = Path.Combine(workspace, "data");
        Directory.CreateDirectory(dataRoot);
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode privateMode = UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(workspace, privateMode);
            File.SetUnixFileMode(dataRoot, privateMode);
        }
    }

    [Fact]
    public async Task CollectionsRetainManualMembershipAcrossRevisionsAndRestarts()
    {
        Guid collectionId, firstSource, secondSource, firstItem;
        using (var catalog = LocalCatalog.Open(dataRoot))
        {
            var first = await Capture(catalog, "first.md", "violet", "one");
            var second = await Capture(catalog, "second.md", "violet", "two");
            var collection = catalog.Collections.Create(" Project Notes ");
            collectionId = collection.CollectionId;
            firstSource = first.SourceId;
            secondSource = second.SourceId;
            firstItem = first.ItemId;

            Assert.Equal("Project Notes", collection.Name);
            Assert.True(catalog.Collections.AddSource(collectionId, firstSource));
            Assert.True(catalog.Collections.AddSource(collectionId, secondSource));
            Assert.False(catalog.Collections.AddSource(collectionId, firstSource));
            Assert.Equal(2, catalog.Library.List(collectionId: collectionId).Count);
            Assert.Single(catalog.Collections.ListForSource(firstSource));

            var revised = await Capture(catalog, "first.md", "amber", "revised");
            Assert.Equal(first.ItemId, revised.ItemId);
            Assert.Equal(first.SourceId, revised.SourceId);
            Assert.Equal(2, revised.Revision);
            Assert.Equal(2, catalog.Library.List(collectionId: collectionId).Count);
            Assert.Contains(catalog.Library.List(collectionId: collectionId),
                item => item.ItemId == firstItem && item.Revision == 2);
        }

        using (var reopened = LocalCatalog.Open(dataRoot))
        {
            var collection = Assert.Single(reopened.Collections.List());
            Assert.Equal(collectionId, collection.CollectionId);
            Assert.Equal(2, collection.ActiveSourceCount);
            Assert.Equal(2, reopened.Library.List(collectionId: collectionId).Count);
            Assert.Equal(new[] { collectionId }, reopened.Collections.ListForSource(firstSource));
            Assert.True(reopened.Collections.RemoveSource(collectionId, secondSource));
            Assert.False(reopened.Collections.RemoveSource(collectionId, secondSource));
            Assert.Single(reopened.Library.List(collectionId: collectionId));
        }

        using var restoredSession = LocalCatalog.Open(dataRoot);
        Assert.Single(restoredSession.Library.List(collectionId: collectionId));
    }

    [Fact]
    public async Task RevokedSourcesCannotLeakThroughCollectionsOrBeReassigned()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var first = await Capture(catalog, "first.md", "shared", "one");
        var second = await Capture(catalog, "second.md", "shared", "two");
        var collection = catalog.Collections.Create("Team");
        Assert.True(catalog.Collections.AddSource(collection.CollectionId, first.SourceId));
        Assert.True(catalog.Collections.AddSource(collection.CollectionId, second.SourceId));
        Assert.True(catalog.Sources.Revoke(first.SourceId));
        Assert.False(catalog.Collections.AddSource(collection.CollectionId, first.SourceId));
        Assert.Empty(catalog.Collections.ListForSource(first.SourceId));
        Assert.Equal(second.SourceId,
            Assert.Single(catalog.Library.List(collectionId: collection.CollectionId)).SourceId);
        Assert.Equal(1, Assert.Single(catalog.Collections.List()).ActiveSourceCount);
        await catalog.Originals.VerifyAsync(first.ItemId);

        Assert.True(catalog.Collections.Archive(collection.CollectionId));
        Assert.False(catalog.Collections.Archive(collection.CollectionId));
        Assert.Empty(catalog.Collections.List());
        Assert.Empty(catalog.Library.List(collectionId: collection.CollectionId));
        Assert.False(catalog.Collections.AddSource(collection.CollectionId, second.SourceId));
    }

    [Fact]
    public async Task CollectionsSurviveCoherentCatalogAndOriginalRestore()
    {
        var backup = Path.Combine(workspace, "backup");
        var destination = Path.Combine(workspace, "restored");
        Guid collectionId, sourceId, itemId;
        using (var catalog = LocalCatalog.Open(dataRoot))
        {
            var item = await Capture(catalog, "book.md", "reviewable", "book");
            var collection = catalog.Collections.Create("Research");
            collectionId = collection.CollectionId;
            sourceId = item.SourceId;
            itemId = item.ItemId;
            Assert.True(catalog.Collections.AddSource(collectionId, sourceId));
            await catalog.Recovery.BackupAsync(backup, 8_000_000);
        }

        await LocalCatalogSnapshots.RestoreAsync(backup, destination, 8_000_000);
        using var reopened = LocalCatalog.Open(destination);
        Assert.Equal(10, reopened.SchemaVersion);
        Assert.Equal(collectionId, Assert.Single(reopened.Collections.List()).CollectionId);
        Assert.Equal(itemId,
            Assert.Single(reopened.Library.List(collectionId: collectionId)).ItemId);
        Assert.Equal(new[] { collectionId }, reopened.Collections.ListForSource(sourceId));
        await reopened.Originals.VerifyAsync(itemId);
    }

    [Fact]
    public void VersionFiveCatalogMigratesWithoutRecreatingSearchSchema()
    {
        var databasePath = Path.Combine(dataRoot, "catalog.sqlite");
        using (var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString()))
        {
            database.Open();
            using var foreignKeys = database.CreateCommand();
            foreignKeys.CommandText = "PRAGMA foreign_keys = ON";
            foreignKeys.ExecuteNonQuery();
            foreach (var version in new[]
                { "LocalCatalog.sql", "LocalCatalog.V2.sql", "LocalCatalog.V3.sql",
                  "LocalCatalog.V4.sql", "LocalCatalog.V5.sql" })
            {
                using var resource = typeof(LocalCatalog).Assembly.GetManifestResourceStream(
                    "Praxis.Storage." + version)!;
                using var reader = new StreamReader(resource);
                using var command = database.CreateCommand();
                command.CommandText = reader.ReadToEnd();
                command.ExecuteNonQuery();
            }
        }

        using var catalog = LocalCatalog.Open(dataRoot);
        Assert.Equal(10, catalog.SchemaVersion);
        Assert.Empty(catalog.Collections.List());
        Assert.Equal("Migrated", catalog.Collections.Create("Migrated").Name);
        Assert.Empty(catalog.Search.Query("missing"));
    }

    [Fact]
    public async Task CollectionNamesMembershipAndPageBoundsAreEnforced()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        Assert.Throws<ArgumentException>(() => catalog.Collections.Create("  "));
        Assert.Throws<ArgumentException>(() => catalog.Collections.Create("Bad\nName"));
        Assert.Throws<ArgumentException>(() => catalog.Collections.Create(new string('x', 81)));
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.Collections.List(201));
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.Collections.List(offset: -1));
        Assert.Throws<ArgumentException>(() => catalog.Collections.AddSource(Guid.Empty, Guid.NewGuid()));

        var first = catalog.Collections.Create("Research");
        Assert.Throws<InvalidOperationException>(() => catalog.Collections.Create(" research "));
        var second = catalog.Collections.Create("Projects");
        Assert.Throws<InvalidOperationException>(() =>
            catalog.Collections.Rename(second.CollectionId, "RESEARCH"));
        Assert.True(catalog.Collections.Rename(second.CollectionId, "Project Notes"));
        Assert.Equal(new[] { "Project Notes", "Research" },
            catalog.Collections.List().Select(c => c.Name).ToArray());
        Assert.Single(catalog.Collections.List(1, 1));

        var source = await Capture(catalog, "data.txt", "content", "one");
        Assert.False(catalog.Collections.AddSource(first.CollectionId, Guid.NewGuid()));
        Assert.True(catalog.Collections.AddSource(first.CollectionId, source.SourceId));
        Assert.True(catalog.Collections.RemoveSource(first.CollectionId, source.SourceId));
        Assert.Empty(catalog.Collections.ListForSource(source.SourceId));
    }

    private async Task<CapturedOriginal> Capture(
        LocalCatalog catalog, string name, string value, string requestKey)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(value));
        return await catalog.Originals.CaptureFileAsync(
            Path.Combine(workspace, name), stream, 1024, requestKey);
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, true);
        }
    }
}
