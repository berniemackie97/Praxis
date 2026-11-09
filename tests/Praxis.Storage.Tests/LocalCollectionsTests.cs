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
            var collection = catalog.CreateCollection(" Project Notes ");
            collectionId = collection.CollectionId;
            firstSource = first.SourceId;
            secondSource = second.SourceId;
            firstItem = first.ItemId;

            Assert.Equal("Project Notes", collection.Name);
            Assert.True(catalog.AddSourceToCollection(collectionId, firstSource));
            Assert.True(catalog.AddSourceToCollection(collectionId, secondSource));
            Assert.False(catalog.AddSourceToCollection(collectionId, firstSource));
            Assert.Equal(2, catalog.ListLibrary(collectionId: collectionId).Count);
            Assert.Single(catalog.ListSourceCollectionIds(firstSource));

            var revised = await Capture(catalog, "first.md", "amber", "revised");
            Assert.Equal(first.ItemId, revised.ItemId);
            Assert.Equal(first.SourceId, revised.SourceId);
            Assert.Equal(2, revised.Revision);
            Assert.Equal(2, catalog.ListLibrary(collectionId: collectionId).Count);
            Assert.Contains(catalog.ListLibrary(collectionId: collectionId),
                item => item.ItemId == firstItem && item.Revision == 2);
        }

        using (var reopened = LocalCatalog.Open(dataRoot))
        {
            var collection = Assert.Single(reopened.ListCollections());
            Assert.Equal(collectionId, collection.CollectionId);
            Assert.Equal(2, collection.ActiveSourceCount);
            Assert.Equal(2, reopened.ListLibrary(collectionId: collectionId).Count);
            Assert.Equal(new[] { collectionId }, reopened.ListSourceCollectionIds(firstSource));
            Assert.True(reopened.RemoveSourceFromCollection(collectionId, secondSource));
            Assert.False(reopened.RemoveSourceFromCollection(collectionId, secondSource));
            Assert.Single(reopened.ListLibrary(collectionId: collectionId));
        }

        using var restoredSession = LocalCatalog.Open(dataRoot);
        Assert.Single(restoredSession.ListLibrary(collectionId: collectionId));
    }

    [Fact]
    public async Task RevokedSourcesCannotLeakThroughCollectionsOrBeReassigned()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var first = await Capture(catalog, "first.md", "shared", "one");
        var second = await Capture(catalog, "second.md", "shared", "two");
        var collection = catalog.CreateCollection("Team");
        Assert.True(catalog.AddSourceToCollection(collection.CollectionId, first.SourceId));
        Assert.True(catalog.AddSourceToCollection(collection.CollectionId, second.SourceId));
        Assert.True(catalog.RevokeSource(first.SourceId));
        Assert.False(catalog.AddSourceToCollection(collection.CollectionId, first.SourceId));
        Assert.Empty(catalog.ListSourceCollectionIds(first.SourceId));
        Assert.Equal(second.SourceId,
            Assert.Single(catalog.ListLibrary(collectionId: collection.CollectionId)).SourceId);
        Assert.Equal(1, Assert.Single(catalog.ListCollections()).ActiveSourceCount);
        await catalog.VerifyOriginalAsync(first.ItemId);

        Assert.True(catalog.ArchiveCollection(collection.CollectionId));
        Assert.False(catalog.ArchiveCollection(collection.CollectionId));
        Assert.Empty(catalog.ListCollections());
        Assert.Empty(catalog.ListLibrary(collectionId: collection.CollectionId));
        Assert.False(catalog.AddSourceToCollection(collection.CollectionId, second.SourceId));
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
            var collection = catalog.CreateCollection("Research");
            collectionId = collection.CollectionId;
            sourceId = item.SourceId;
            itemId = item.ItemId;
            Assert.True(catalog.AddSourceToCollection(collectionId, sourceId));
            await catalog.BackupAsync(backup, 8_000_000);
        }

        await LocalCatalog.RestoreAsync(backup, destination, 8_000_000);
        using var reopened = LocalCatalog.Open(destination);
        Assert.Equal(7, reopened.SchemaVersion);
        Assert.Equal(collectionId, Assert.Single(reopened.ListCollections()).CollectionId);
        Assert.Equal(itemId,
            Assert.Single(reopened.ListLibrary(collectionId: collectionId)).ItemId);
        Assert.Equal(new[] { collectionId }, reopened.ListSourceCollectionIds(sourceId));
        await reopened.VerifyOriginalAsync(itemId);
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
        Assert.Equal(7, catalog.SchemaVersion);
        Assert.Empty(catalog.ListCollections());
        Assert.Equal("Migrated", catalog.CreateCollection("Migrated").Name);
        Assert.Empty(catalog.SearchText("missing"));
    }

    [Fact]
    public async Task CollectionNamesMembershipAndPageBoundsAreEnforced()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        Assert.Throws<ArgumentException>(() => catalog.CreateCollection("  "));
        Assert.Throws<ArgumentException>(() => catalog.CreateCollection("Bad\nName"));
        Assert.Throws<ArgumentException>(() => catalog.CreateCollection(new string('x', 81)));
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.ListCollections(201));
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.ListCollections(offset: -1));
        Assert.Throws<ArgumentException>(() => catalog.AddSourceToCollection(Guid.Empty, Guid.NewGuid()));

        var first = catalog.CreateCollection("Research");
        Assert.Throws<InvalidOperationException>(() => catalog.CreateCollection(" research "));
        var second = catalog.CreateCollection("Projects");
        Assert.Throws<InvalidOperationException>(() =>
            catalog.RenameCollection(second.CollectionId, "RESEARCH"));
        Assert.True(catalog.RenameCollection(second.CollectionId, "Project Notes"));
        Assert.Equal(new[] { "Project Notes", "Research" },
            catalog.ListCollections().Select(c => c.Name).ToArray());
        Assert.Single(catalog.ListCollections(1, 1));

        var source = await Capture(catalog, "data.txt", "content", "one");
        Assert.False(catalog.AddSourceToCollection(first.CollectionId, Guid.NewGuid()));
        Assert.True(catalog.AddSourceToCollection(first.CollectionId, source.SourceId));
        Assert.True(catalog.RemoveSourceFromCollection(first.CollectionId, source.SourceId));
        Assert.Empty(catalog.ListSourceCollectionIds(source.SourceId));
    }

    private async Task<CapturedOriginal> Capture(
        LocalCatalog catalog, string name, string value, string requestKey)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(value));
        return await catalog.CaptureFileAsync(
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
