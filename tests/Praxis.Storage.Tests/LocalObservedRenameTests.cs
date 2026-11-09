using Microsoft.Data.Sqlite;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class LocalObservedRenameTests : IDisposable
{
    private readonly string workspace = Path.Combine(
        Path.GetTempPath(), "praxis-renames-" + Guid.NewGuid().ToString("N"));
    private readonly string dataRoot;
    private readonly string inbox;

    public LocalObservedRenameTests()
    {
        dataRoot = Path.Combine(workspace, "data");
        inbox = Path.Combine(workspace, "inbox");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(inbox);
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode mode = UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(workspace, mode);
            File.SetUnixFileMode(dataRoot, mode);
            File.SetUnixFileMode(inbox, mode);
        }
    }

    [Fact]
    public async Task ObservedRenamePreservesIdentityCollectionsSearchAndFutureRevisions()
    {
        var oldPath = Path.Combine(inbox, "before.md");
        var newPath = Path.Combine(inbox, "after.md");
        File.WriteAllText(oldPath, "# Project\nviolet preserved note");
        using var catalog = LocalCatalog.Open(dataRoot);
        var watcher = NewWatcher(catalog);
        var time = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(time);
        Assert.Equal(1, await watcher.ReconcileAsync(time.AddSeconds(2)));
        var before = Assert.Single(catalog.ListLibrary());
        var group = catalog.CreateCollection("Research");
        Assert.True(catalog.AddSourceToCollection(group.CollectionId, before.SourceId));

        File.Move(oldPath, newPath);
        watcher.QueueRename(oldPath, newPath);
        Assert.Equal(0, await watcher.ReconcileAsync(time.AddSeconds(3)));
        Assert.Equal(0, await watcher.ReconcileAsync(time.AddSeconds(5)));
        var after = Assert.Single(catalog.ListLibrary());
        Assert.Equal(before.SourceId, after.SourceId);
        Assert.Equal(before.ItemId, after.ItemId);
        Assert.Equal(before.Sha256, after.Sha256);
        Assert.Equal(1, after.Revision);
        Assert.Equal(newPath, after.SourceLocator);
        Assert.Null(catalog.FindLatestFile(oldPath));
        Assert.Equal(before.SourceId, catalog.FindLatestFile(newPath)?.SourceId);
        Assert.Equal(before.ItemId, Assert.Single(catalog.SearchText("violet")).ItemId);
        Assert.Equal(before.ItemId,
            Assert.Single(catalog.ListLibrary(collectionId: group.CollectionId)).ItemId);
        Assert.Equal(1L, Count("source_locator_moves"));
        var movement = Assert.Single(catalog.ListSourceMovements(before.SourceId));
        Assert.Equal(oldPath, movement.PreviousPath);
        Assert.Equal(newPath, movement.CurrentPath);
        Assert.Equal(before.Sha256, movement.Sha256);
        Assert.Empty(catalog.ListSourceMovements(Guid.NewGuid()));
        Assert.Equal(1L, Count("item_versions"));
        Assert.Equal(1L, Count("ingest_requests"));
        await catalog.VerifyOriginalAsync(before.ItemId);

        File.WriteAllText(newPath, "# Project\namber revision");
        File.SetLastWriteTimeUtc(newPath, DateTime.UtcNow.AddSeconds(10));
        await watcher.ReconcileAsync(time.AddSeconds(6));
        Assert.Equal(1, await watcher.ReconcileAsync(time.AddSeconds(8)));
        var revised = Assert.Single(catalog.ListLibrary());
        Assert.Equal(before.SourceId, revised.SourceId);
        Assert.Equal(before.ItemId, revised.ItemId);
        Assert.Equal(2, revised.Revision);
        Assert.Empty(catalog.SearchText("violet"));
        Assert.Equal(before.ItemId, Assert.Single(catalog.SearchText("amber")).ItemId);
        Assert.Equal(1L, Count("source_locator_moves"));
    }

    [Fact]
    public async Task ReusingMovedFromPathCreatesAnIndependentSourceWithoutReceiptCollision()
    {
        var oldPath = Path.Combine(inbox, "original.md");
        var movedPath = Path.Combine(inbox, "moved.md");
        File.WriteAllText(oldPath, "same preserved bytes");
        using var catalog = LocalCatalog.Open(dataRoot);
        var watcher = NewWatcher(catalog);
        var now = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(now);
        await watcher.ReconcileAsync(now.AddSeconds(2));
        var original = Assert.Single(catalog.ListLibrary());

        File.Move(oldPath, movedPath);
        watcher.QueueRename(oldPath, movedPath);
        await watcher.ReconcileAsync(now.AddSeconds(3));
        Assert.Equal(original.SourceId, catalog.FindLatestFile(movedPath)?.SourceId);

        File.WriteAllText(oldPath, "same preserved bytes");
        await watcher.ReconcileAsync(now.AddSeconds(4));
        Assert.Equal(1, await watcher.ReconcileAsync(now.AddSeconds(6)));
        var recreated = catalog.FindLatestFile(oldPath);
        Assert.NotNull(recreated);
        Assert.NotEqual(original.SourceId, recreated.SourceId);
        Assert.NotEqual(original.ItemId, recreated.ItemId);
        Assert.Equal(original.Sha256, recreated.Sha256);
        Assert.Equal(1L, Count("source_locator_moves"));
        Assert.Equal(2L, Count("ingest_requests"));
        await catalog.VerifyOriginalAsync(original.ItemId);
        await catalog.VerifyOriginalAsync(recreated.ItemId);
    }

    [Fact]
    public async Task RevokedSourceIdentityCannotTransferAcrossObservedRename()
    {
        var oldPath = Path.Combine(inbox, "revoked.md");
        var movedPath = Path.Combine(inbox, "new-source.md");
        File.WriteAllText(oldPath, "revoked original");
        using var catalog = LocalCatalog.Open(dataRoot);
        var watcher = NewWatcher(catalog);
        var now = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(now);
        await watcher.ReconcileAsync(now.AddSeconds(2));
        var original = Assert.Single(catalog.ListLibrary());
        Assert.True(catalog.RevokeSource(original.SourceId));

        File.Move(oldPath, movedPath);
        watcher.QueueRename(oldPath, movedPath);
        await watcher.ReconcileAsync(now.AddSeconds(3));
        await watcher.ReconcileAsync(now.AddSeconds(5));

        var current = catalog.FindLatestFile(movedPath);
        Assert.NotNull(current);
        Assert.NotEqual(original.SourceId, current.SourceId);
        Assert.Equal(0L, Count("source_locator_moves"));
        Assert.Equal("revoked", catalog.FindLatestFile(oldPath)?.Status);
        Assert.Empty(catalog.ListSourceMovements(original.SourceId));
        await catalog.VerifyOriginalAsync(original.ItemId);
    }

    [Fact]
    public async Task SameContentCopyWithoutObservedRenameRemainsIndependent()
    {
        var first = Path.Combine(inbox, "original.md");
        var second = Path.Combine(inbox, "copy.md");
        File.WriteAllText(first, "violet same bytes");
        using var catalog = LocalCatalog.Open(dataRoot);
        var watcher = NewWatcher(catalog);
        var time = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(time);
        await watcher.ReconcileAsync(time.AddSeconds(2));
        var captured = Assert.Single(catalog.ListLibrary());

        File.Copy(first, second);
        await watcher.ReconcileAsync(time.AddSeconds(3));
        Assert.Equal(1, await watcher.ReconcileAsync(time.AddSeconds(5)));
        var both = catalog.ListLibrary();
        Assert.Equal(2, both.Count);
        Assert.NotEqual(both[0].SourceId, both[1].SourceId);
        Assert.Equal(captured.Sha256, both[0].Sha256);
        Assert.Equal(captured.Sha256, both[1].Sha256);
        Assert.Equal(0L, Count("source_locator_moves"));
    }

    [Fact]
    public async Task MissedRenameDoesNotGuessIdentityFromMatchingDigest()
    {
        var first = Path.Combine(inbox, "before.txt");
        var next = Path.Combine(inbox, "after.txt");
        File.WriteAllText(first, "identical bytes");
        using var catalog = LocalCatalog.Open(dataRoot);
        var watcher = NewWatcher(catalog);
        var time = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(time);
        await watcher.ReconcileAsync(time.AddSeconds(2));
        var original = Assert.Single(catalog.ListLibrary());

        File.Move(first, next);
        await watcher.ReconcileAsync(time.AddSeconds(3));
        await watcher.ReconcileAsync(time.AddSeconds(5));
        var resulting = catalog.ListLibrary();
        Assert.Equal(2, resulting.Count);
        Assert.Contains(resulting, item => item.SourceId == original.SourceId &&
            item.SourceLocator == first);
        Assert.Contains(resulting, item => item.SourceId != original.SourceId &&
            item.SourceLocator == next);
        Assert.Equal(0L, Count("source_locator_moves"));
    }

    [Fact]
    public async Task OverflowedEventQueueFallsBackToIndependentCapture()
    {
        var first = Path.Combine(inbox, "first.md");
        var next = Path.Combine(inbox, "next.md");
        File.WriteAllText(first, "# Same topic");
        using var catalog = LocalCatalog.Open(dataRoot);
        var reports = new List<string>();
        var monitor = new LocalWatchedIntake(catalog, inbox,
            TimeSpan.FromSeconds(1), status: reports.Add);
        var now = DateTimeOffset.UtcNow;
        await monitor.ReconcileAsync(now);
        await monitor.ReconcileAsync(now.AddSeconds(2));
        var before = Assert.Single(catalog.ListLibrary());

        File.Move(first, next);
        for (var index = 0; index < 129; index++)
        {
            monitor.QueueRename(first, next);
        }

        await monitor.ReconcileAsync(now.AddSeconds(3));
        Assert.Equal(1, await monitor.ReconcileAsync(now.AddSeconds(5)));
        var after = catalog.FindLatestFile(next);
        Assert.NotNull(after);
        Assert.NotEqual(before.SourceId, after.SourceId);
        Assert.Equal(0L, Count("source_locator_moves"));
        Assert.Contains(reports, value => value.Contains(
            "Rename notification backlog exceeded", StringComparison.Ordinal));
        await catalog.VerifyOriginalAsync(before.ItemId);
    }

    [Fact]
    public async Task ExistingOldPathPreventsIdentityTransferEvenWithAQueuedEvent()
    {
        var first = Path.Combine(inbox, "before.md");
        var second = Path.Combine(inbox, "copy.md");
        File.WriteAllText(first, "# Shared");
        using var catalog = LocalCatalog.Open(dataRoot);
        var watcher = NewWatcher(catalog);
        var now = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(now);
        await watcher.ReconcileAsync(now.AddSeconds(2));
        var original = Assert.Single(catalog.ListLibrary());

        File.Copy(first, second);
        watcher.QueueRename(first, second);
        await watcher.ReconcileAsync(now.AddSeconds(3));
        await watcher.ReconcileAsync(now.AddSeconds(5));
        Assert.NotEqual(original.SourceId, catalog.FindLatestFile(second)?.SourceId);
        Assert.Equal(0L, Count("source_locator_moves"));
    }

    [Fact]
    public async Task ChangedDestinationCannotInheritPriorSourceIdentity()
    {
        var first = Path.Combine(inbox, "old.md");
        var next = Path.Combine(inbox, "new.md");
        File.WriteAllText(first, "# Old\nviolet");
        using var catalog = LocalCatalog.Open(dataRoot);
        var watcher = NewWatcher(catalog);
        var time = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(time);
        await watcher.ReconcileAsync(time.AddSeconds(2));
        var prior = Assert.Single(catalog.ListLibrary());

        File.Move(first, next);
        File.WriteAllText(next, "# Changed\namber");
        watcher.QueueRename(first, next);
        await watcher.ReconcileAsync(time.AddSeconds(3));
        await watcher.ReconcileAsync(time.AddSeconds(5));

        var current = catalog.FindLatestFile(next);
        Assert.NotNull(current);
        Assert.NotEqual(prior.SourceId, current.SourceId);
        Assert.Equal(0L, Count("source_locator_moves"));
        Assert.Equal(prior.ItemId, Assert.Single(catalog.SearchText("violet")).ItemId);
        Assert.Equal(current.ItemId, Assert.Single(catalog.SearchText("amber")).ItemId);
    }

    [Fact]
    public async Task OccupiedDestinationIsNeverReassignedToMovedSource()
    {
        var first = Path.Combine(inbox, "first.txt");
        var second = Path.Combine(inbox, "second.txt");
        File.WriteAllText(first, "first violet");
        File.WriteAllText(second, "second amber");
        using var catalog = LocalCatalog.Open(dataRoot);
        var watcher = NewWatcher(catalog);
        var time = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(time);
        await watcher.ReconcileAsync(time.AddSeconds(2));
        var sources = catalog.ListLibrary();
        var old = Assert.Single(sources, item => item.SourceLocator == first);
        var occupied = Assert.Single(sources, item => item.SourceLocator == second);

        File.Move(first, second, overwrite: true);
        watcher.QueueRename(first, second);
        await watcher.ReconcileAsync(time.AddSeconds(3));
        await watcher.ReconcileAsync(time.AddSeconds(5));
        Assert.Equal(old.SourceId, catalog.FindLatestFile(first)?.SourceId);
        Assert.Equal(occupied.SourceId, catalog.FindLatestFile(second)?.SourceId);
        Assert.Equal(2, catalog.FindLatestFile(second)?.Revision);
        Assert.Equal(0L, Count("source_locator_moves"));
    }

    [Fact]
    public async Task RenameHistorySurvivesCoherentSnapshotRestore()
    {
        var oldPath = Path.Combine(inbox, "old.md");
        var newPath = Path.Combine(inbox, "moved.md");
        var backup = Path.Combine(workspace, "backup");
        var restoredRoot = Path.Combine(workspace, "restored");
        Guid sourceId;
        Guid itemId;
        using (var catalog = LocalCatalog.Open(dataRoot))
        {
            File.WriteAllText(oldPath, "restorable topic");
            var watcher = NewWatcher(catalog);
            var time = DateTimeOffset.UtcNow;
            await watcher.ReconcileAsync(time);
            await watcher.ReconcileAsync(time.AddSeconds(2));
            var captured = Assert.Single(catalog.ListLibrary());
            sourceId = captured.SourceId;
            itemId = captured.ItemId;

            File.Move(oldPath, newPath);
            watcher.QueueRename(oldPath, newPath);
            await watcher.ReconcileAsync(time.AddSeconds(3));
            Assert.Equal(1L, Count("source_locator_moves"));
            await catalog.BackupAsync(backup, 8_000_000);
        }

        await LocalCatalog.RestoreAsync(backup, restoredRoot, 8_000_000);
        using var restored = LocalCatalog.Open(restoredRoot);
        Assert.Equal(8, restored.SchemaVersion);
        var recovered = Assert.Single(restored.ListLibrary());
        Assert.Equal(sourceId, recovered.SourceId);
        Assert.Equal(itemId, recovered.ItemId);
        Assert.Equal(newPath, recovered.SourceLocator);
        await restored.VerifyOriginalAsync(itemId);
        Assert.Equal(1L, Count("source_locator_moves", restored.DatabasePath));
    }

    [Fact]
    public void VersionSevenUpgradeAddsMoveJournalWithoutChangingSourceAuthority()
    {
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataRoot, "catalog.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString()))
        {
            connection.Open();
            foreach (var name in new[] { "LocalCatalog.sql", "LocalCatalog.V2.sql",
                "LocalCatalog.V3.sql", "LocalCatalog.V4.sql", "LocalCatalog.V5.sql",
                "LocalCatalog.V6.sql", "LocalCatalog.V7.sql" })
            {
                using var resource = typeof(LocalCatalog).Assembly.GetManifestResourceStream(
                    "Praxis.Storage." + name)!;
                using var reader = new StreamReader(resource);
                using var command = connection.CreateCommand();
                command.CommandText = reader.ReadToEnd();
                command.ExecuteNonQuery();
            }
        }

        using var upgraded = LocalCatalog.Open(dataRoot);
        Assert.Equal(8, upgraded.SchemaVersion);
        Assert.Equal(0L, Count("source_locator_moves"));
    }

    private LocalWatchedIntake NewWatcher(LocalCatalog catalog) =>
        new(catalog, inbox, TimeSpan.FromSeconds(1));

    private long Count(string table, string? databasePath = null)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath ?? Path.Combine(dataRoot, "catalog.sqlite"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
