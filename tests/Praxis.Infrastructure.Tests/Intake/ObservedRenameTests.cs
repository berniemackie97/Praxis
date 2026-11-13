using Microsoft.Data.Sqlite;
using Praxis.Domain.Collections;
using Praxis.Domain.Library;
using Praxis.Domain.Sources;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Persistence.Recovery;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Intake;

public sealed class ObservedRenameTests : IDisposable
{
    private readonly string workspace = Path.Combine(
        TestPaths.Temp, "praxis-renames-" + Guid.NewGuid().ToString("N"));
    private readonly string dataRoot;
    private readonly string inbox;

    public ObservedRenameTests()
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
        string oldPath = Path.Combine(inbox, "before.md");
        string newPath = Path.Combine(inbox, "after.md");
        File.WriteAllText(oldPath, "# Project\nviolet preserved note");
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        IntakeHarness watcher = NewWatcher(catalog);
        DateTimeOffset time = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(time);
        Assert.Equal(1, await watcher.ReconcileAsync(time.AddSeconds(2)));
        LibraryItem before = Assert.Single(catalog.Library.List());
        SourceCollection group = catalog.Collections.Create("Research");
        Assert.True(catalog.Collections.AddSource(group.CollectionId, before.SourceId));

        File.Move(oldPath, newPath);
        watcher.Monitor.QueueRename(oldPath, newPath);
        Assert.Equal(0, await watcher.ReconcileAsync(time.AddSeconds(3)));
        Assert.Equal(0, await watcher.ReconcileAsync(time.AddSeconds(5)));
        LibraryItem after = Assert.Single(catalog.Library.List());
        Assert.Equal(before.SourceId, after.SourceId);
        Assert.Equal(before.ItemId, after.ItemId);
        Assert.Equal(before.Sha256, after.Sha256);
        Assert.Equal(1, after.Revision);
        Assert.Equal(newPath, after.SourceLocator);
        Assert.Null(catalog.Sources.FindLatestFile(oldPath));
        Assert.Equal(before.SourceId, catalog.Sources.FindLatestFile(newPath)?.SourceId);
        Assert.Equal(before.ItemId, Assert.Single(catalog.Search.Query("violet")).ItemId);
        Assert.Equal(before.ItemId,
            Assert.Single(catalog.Library.List(collectionId: group.CollectionId)).ItemId);
        Assert.Equal(1L, Count("source_locator_moves"));
        SourceMovement movement = Assert.Single(catalog.Sources.ListMovements(before.SourceId));
        Assert.Equal(oldPath, movement.PreviousPath);
        Assert.Equal(newPath, movement.CurrentPath);
        Assert.Equal(before.Sha256, movement.Sha256);
        Assert.Empty(catalog.Sources.ListMovements(Guid.NewGuid()));
        Assert.Equal(1L, Count("item_versions"));
        Assert.Equal(1L, Count("ingest_requests"));
        await catalog.Originals.VerifyAsync(before.ItemId);

        File.WriteAllText(newPath, "# Project\namber revision");
        File.SetLastWriteTimeUtc(newPath, DateTime.UtcNow.AddSeconds(10));
        await watcher.ReconcileAsync(time.AddSeconds(6));
        Assert.Equal(1, await watcher.ReconcileAsync(time.AddSeconds(8)));
        LibraryItem revised = Assert.Single(catalog.Library.List());
        Assert.Equal(before.SourceId, revised.SourceId);
        Assert.Equal(before.ItemId, revised.ItemId);
        Assert.Equal(2, revised.Revision);
        Assert.Empty(catalog.Search.Query("violet"));
        Assert.Equal(before.ItemId, Assert.Single(catalog.Search.Query("amber")).ItemId);
        Assert.Equal(1L, Count("source_locator_moves"));
    }

    [Fact]
    public async Task ReusingMovedFromPathCreatesAnIndependentSourceWithoutReceiptCollision()
    {
        string oldPath = Path.Combine(inbox, "original.md");
        string movedPath = Path.Combine(inbox, "moved.md");
        File.WriteAllText(oldPath, "same preserved bytes");
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        IntakeHarness watcher = NewWatcher(catalog);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(now);
        await watcher.ReconcileAsync(now.AddSeconds(2));
        LibraryItem original = Assert.Single(catalog.Library.List());

        File.Move(oldPath, movedPath);
        watcher.Monitor.QueueRename(oldPath, movedPath);
        await watcher.ReconcileAsync(now.AddSeconds(3));
        Assert.Equal(original.SourceId, catalog.Sources.FindLatestFile(movedPath)?.SourceId);

        File.WriteAllText(oldPath, "same preserved bytes");
        await watcher.ReconcileAsync(now.AddSeconds(4));
        Assert.Equal(1, await watcher.ReconcileAsync(now.AddSeconds(6)));
        SourceFileVersion? recreated = catalog.Sources.FindLatestFile(oldPath);
        Assert.NotNull(recreated);
        Assert.NotEqual(original.SourceId, recreated.SourceId);
        Assert.NotEqual(original.ItemId, recreated.ItemId);
        Assert.Equal(original.Sha256, recreated.Sha256);
        Assert.Equal(1L, Count("source_locator_moves"));
        Assert.Equal(2L, Count("ingest_requests"));
        await catalog.Originals.VerifyAsync(original.ItemId);
        await catalog.Originals.VerifyAsync(recreated.ItemId);
    }

    [Fact]
    public async Task RevokedSourceIdentityCannotTransferAcrossObservedRename()
    {
        string oldPath = Path.Combine(inbox, "revoked.md");
        string movedPath = Path.Combine(inbox, "new-source.md");
        File.WriteAllText(oldPath, "revoked original");
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        IntakeHarness watcher = NewWatcher(catalog);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(now);
        await watcher.ReconcileAsync(now.AddSeconds(2));
        LibraryItem original = Assert.Single(catalog.Library.List());
        Assert.True(catalog.Sources.Revoke(original.SourceId));

        File.Move(oldPath, movedPath);
        watcher.Monitor.QueueRename(oldPath, movedPath);
        await watcher.ReconcileAsync(now.AddSeconds(3));
        await watcher.ReconcileAsync(now.AddSeconds(5));

        SourceFileVersion? current = catalog.Sources.FindLatestFile(movedPath);
        Assert.NotNull(current);
        Assert.NotEqual(original.SourceId, current.SourceId);
        Assert.Equal(0L, Count("source_locator_moves"));
        Assert.Equal("revoked", catalog.Sources.FindLatestFile(oldPath)?.Status);
        Assert.Empty(catalog.Sources.ListMovements(original.SourceId));
        await catalog.Originals.VerifyAsync(original.ItemId);
    }

    [Fact]
    public async Task SameContentCopyWithoutObservedRenameRemainsIndependent()
    {
        string first = Path.Combine(inbox, "original.md");
        string second = Path.Combine(inbox, "copy.md");
        File.WriteAllText(first, "violet same bytes");
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        IntakeHarness watcher = NewWatcher(catalog);
        DateTimeOffset time = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(time);
        await watcher.ReconcileAsync(time.AddSeconds(2));
        LibraryItem captured = Assert.Single(catalog.Library.List());

        File.Copy(first, second);
        await watcher.ReconcileAsync(time.AddSeconds(3));
        Assert.Equal(1, await watcher.ReconcileAsync(time.AddSeconds(5)));
        IReadOnlyList<LibraryItem> both = catalog.Library.List();
        Assert.Equal(2, both.Count);
        Assert.NotEqual(both[0].SourceId, both[1].SourceId);
        Assert.Equal(captured.Sha256, both[0].Sha256);
        Assert.Equal(captured.Sha256, both[1].Sha256);
        Assert.Equal(0L, Count("source_locator_moves"));
    }

    [Fact]
    public async Task MissedRenameDoesNotGuessIdentityFromMatchingDigest()
    {
        string first = Path.Combine(inbox, "before.txt");
        string next = Path.Combine(inbox, "after.txt");
        File.WriteAllText(first, "identical bytes");
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        IntakeHarness watcher = NewWatcher(catalog);
        DateTimeOffset time = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(time);
        await watcher.ReconcileAsync(time.AddSeconds(2));
        LibraryItem original = Assert.Single(catalog.Library.List());

        File.Move(first, next);
        await watcher.ReconcileAsync(time.AddSeconds(3));
        await watcher.ReconcileAsync(time.AddSeconds(5));
        IReadOnlyList<LibraryItem> resulting = catalog.Library.List();
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
        string first = Path.Combine(inbox, "first.md");
        string next = Path.Combine(inbox, "next.md");
        File.WriteAllText(first, "# Same topic");
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        List<string> reports = new();
        IntakeHarness monitor = new(catalog, inbox,
            TimeSpan.FromSeconds(1), status: reports.Add);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await monitor.ReconcileAsync(now);
        await monitor.ReconcileAsync(now.AddSeconds(2));
        LibraryItem before = Assert.Single(catalog.Library.List());

        File.Move(first, next);
        for (int index = 0; index < 129; index++)
        {
            monitor.Monitor.QueueRename(first, next);
        }

        await monitor.ReconcileAsync(now.AddSeconds(3));
        Assert.Equal(1, await monitor.ReconcileAsync(now.AddSeconds(5)));
        SourceFileVersion? after = catalog.Sources.FindLatestFile(next);
        Assert.NotNull(after);
        Assert.NotEqual(before.SourceId, after.SourceId);
        Assert.Equal(0L, Count("source_locator_moves"));
        Assert.Contains(reports, value => value.Contains(
            "Rename notification backlog exceeded", StringComparison.Ordinal));
        await catalog.Originals.VerifyAsync(before.ItemId);
    }

    [Fact]
    public async Task ExistingOldPathPreventsIdentityTransferEvenWithAQueuedEvent()
    {
        string first = Path.Combine(inbox, "before.md");
        string second = Path.Combine(inbox, "copy.md");
        File.WriteAllText(first, "# Shared");
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        IntakeHarness watcher = NewWatcher(catalog);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(now);
        await watcher.ReconcileAsync(now.AddSeconds(2));
        LibraryItem original = Assert.Single(catalog.Library.List());

        File.Copy(first, second);
        watcher.Monitor.QueueRename(first, second);
        await watcher.ReconcileAsync(now.AddSeconds(3));
        await watcher.ReconcileAsync(now.AddSeconds(5));
        Assert.NotEqual(original.SourceId, catalog.Sources.FindLatestFile(second)?.SourceId);
        Assert.Equal(0L, Count("source_locator_moves"));
    }

    [Fact]
    public async Task ChangedDestinationCannotInheritPriorSourceIdentity()
    {
        string first = Path.Combine(inbox, "old.md");
        string next = Path.Combine(inbox, "new.md");
        File.WriteAllText(first, "# Old\nviolet");
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        IntakeHarness watcher = NewWatcher(catalog);
        DateTimeOffset time = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(time);
        await watcher.ReconcileAsync(time.AddSeconds(2));
        LibraryItem prior = Assert.Single(catalog.Library.List());

        File.Move(first, next);
        File.WriteAllText(next, "# Changed\namber");
        watcher.Monitor.QueueRename(first, next);
        await watcher.ReconcileAsync(time.AddSeconds(3));
        await watcher.ReconcileAsync(time.AddSeconds(5));

        SourceFileVersion? current = catalog.Sources.FindLatestFile(next);
        Assert.NotNull(current);
        Assert.NotEqual(prior.SourceId, current.SourceId);
        Assert.Equal(0L, Count("source_locator_moves"));
        Assert.Equal(prior.ItemId, Assert.Single(catalog.Search.Query("violet")).ItemId);
        Assert.Equal(current.ItemId, Assert.Single(catalog.Search.Query("amber")).ItemId);
    }

    [Fact]
    public async Task OccupiedDestinationIsNeverReassignedToMovedSource()
    {
        string first = Path.Combine(inbox, "first.txt");
        string second = Path.Combine(inbox, "second.txt");
        File.WriteAllText(first, "first violet");
        File.WriteAllText(second, "second amber");
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        IntakeHarness watcher = NewWatcher(catalog);
        DateTimeOffset time = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(time);
        await watcher.ReconcileAsync(time.AddSeconds(2));
        IReadOnlyList<LibraryItem> sources = catalog.Library.List();
        LibraryItem old = Assert.Single(sources, item => item.SourceLocator == first);
        LibraryItem occupied = Assert.Single(sources, item => item.SourceLocator == second);

        File.Move(first, second, overwrite: true);
        watcher.Monitor.QueueRename(first, second);
        await watcher.ReconcileAsync(time.AddSeconds(3));
        await watcher.ReconcileAsync(time.AddSeconds(5));
        Assert.Equal(old.SourceId, catalog.Sources.FindLatestFile(first)?.SourceId);
        Assert.Equal(occupied.SourceId, catalog.Sources.FindLatestFile(second)?.SourceId);
        Assert.Equal(2, catalog.Sources.FindLatestFile(second)?.Revision);
        Assert.Equal(0L, Count("source_locator_moves"));
    }


    [Fact]
    public async Task ObservedDirectoryMovePreservesNestedSourcesAndCollectionMembership()
    {
        string previous = Path.Combine(inbox, "previous");
        string nested = Path.Combine(previous, "nested");
        Directory.CreateDirectory(nested);
        string first = Path.Combine(previous, "first.md");
        string second = Path.Combine(nested, "second.txt");
        File.WriteAllText(first, "violet folder note");
        File.WriteAllText(second, "amber nested note");

        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        IntakeHarness watcher = NewWatcher(catalog);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(now);
        Assert.Equal(2, await watcher.ReconcileAsync(now.AddSeconds(2)));
        LibraryItem[] original = catalog.Library.List().OrderBy(item => item.SourceLocator).ToArray();
        Assert.Equal(2, original.Length);
        SourceCollection group = catalog.Collections.Create("Moved sources");
        Assert.True(catalog.Collections.AddSource(group.CollectionId, original[0].SourceId));

        string destination = Path.Combine(inbox, "destination");
        Directory.Move(previous, destination);
        watcher.Monitor.QueueRename(previous, destination);
        Assert.Equal(0, await watcher.ReconcileAsync(now.AddSeconds(3)));
        Assert.Equal(0, await watcher.ReconcileAsync(now.AddSeconds(5)));
        LibraryItem[] moved = catalog.Library.List().OrderBy(item => item.SourceLocator).ToArray();
        Assert.Equal(2, moved.Length);
        Assert.Equal(original.Select(item => item.SourceId).OrderBy(id => id), moved.Select(item => item.SourceId).OrderBy(id => id));
        Assert.Equal(original.Select(item => item.ItemId).OrderBy(id => id), moved.Select(item => item.ItemId).OrderBy(id => id));
        Assert.All(moved, item => Assert.Equal(1, item.Revision));
        Assert.All(moved, item => Assert.StartsWith(destination + Path.DirectorySeparatorChar, item.SourceLocator));
        Assert.Equal(2L, Count("source_locator_moves"));
        Assert.Single(catalog.Library.List(collectionId: group.CollectionId));
        Assert.Single(catalog.Search.Query("violet"));
        Assert.Single(catalog.Search.Query("amber"));
        foreach (LibraryItem item in moved)
        {
            await catalog.Originals.VerifyAsync(item.ItemId);
        }

        string revised = Path.Combine(destination, "nested", "second.txt");
        File.WriteAllText(revised, "cobalt nested revision");
        File.SetLastWriteTimeUtc(revised, DateTime.UtcNow.AddSeconds(20));
        Assert.Equal(0, await watcher.ReconcileAsync(now.AddSeconds(6)));
        Assert.Equal(1, await watcher.ReconcileAsync(now.AddSeconds(8)));
        LibraryItem updated = Assert.Single(catalog.Library.List(), item => item.SourceLocator == revised);
        Assert.Equal(2, updated.Revision);
        Assert.Empty(catalog.Search.Query("amber"));
        Assert.Single(catalog.Search.Query("cobalt"));
    }

    [Fact]
    public async Task DirectoryCopyCannotInheritIdentitiesEvenWithSyntheticMoveHint()
    {
        string previous = Path.Combine(inbox, "original");
        string destination = Path.Combine(inbox, "copy");
        Directory.CreateDirectory(previous);
        string path = Path.Combine(previous, "note.md");
        File.WriteAllText(path, "violet independent copy");

        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        IntakeHarness watcher = NewWatcher(catalog);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(now);
        await watcher.ReconcileAsync(now.AddSeconds(2));
        LibraryItem first = Assert.Single(catalog.Library.List());

        Directory.CreateDirectory(destination);
        File.Copy(path, Path.Combine(destination, "note.md"));
        watcher.Monitor.QueueRename(previous, destination);
        await watcher.ReconcileAsync(now.AddSeconds(3));
        Assert.Equal(1, await watcher.ReconcileAsync(now.AddSeconds(5)));
        LibraryItem[] items = catalog.Library.List().ToArray();
        Assert.Equal(2, items.Length);
        Assert.NotEqual(first.SourceId, Assert.Single(items, item => item.SourceLocator.StartsWith(destination, StringComparison.Ordinal)).SourceId);
        Assert.Equal(0L, Count("source_locator_moves"));
    }


    [Fact]
    public async Task MissedDirectoryMoveEventNeverGuessesContinuity()
    {
        string previous = Path.Combine(inbox, "before");
        string destination = Path.Combine(inbox, "after");
        Directory.CreateDirectory(previous);
        File.WriteAllText(Path.Combine(previous, "note.md"), "violet unobserved move");
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        IntakeHarness watcher = NewWatcher(catalog);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(now);
        await watcher.ReconcileAsync(now.AddSeconds(2));
        LibraryItem prior = Assert.Single(catalog.Library.List());

        Directory.Move(previous, destination);
        await watcher.ReconcileAsync(now.AddSeconds(3));
        Assert.Equal(1, await watcher.ReconcileAsync(now.AddSeconds(5)));
        LibraryItem[] items = catalog.Library.List().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal(prior.SourceId, Assert.Single(items, item => item.SourceLocator.StartsWith(previous, StringComparison.Ordinal)).SourceId);
        Assert.NotEqual(prior.SourceId, Assert.Single(items, item => item.SourceLocator.StartsWith(destination, StringComparison.Ordinal)).SourceId);
        Assert.Equal(0L, Count("source_locator_moves"));
    }

    [Fact]
    public async Task ChangedFileInsideMovedDirectoryDoesNotInheritOldIdentity()
    {
        string previous = Path.Combine(inbox, "old");
        string destination = Path.Combine(inbox, "new");
        Directory.CreateDirectory(previous);
        string path = Path.Combine(previous, "note.md");
        File.WriteAllText(path, "violet original file");
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        IntakeHarness watcher = NewWatcher(catalog);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(now);
        await watcher.ReconcileAsync(now.AddSeconds(2));
        LibraryItem first = Assert.Single(catalog.Library.List());

        Directory.Move(previous, destination);
        string movedPath = Path.Combine(destination, "note.md");
        File.WriteAllText(movedPath, "orange modified file");
        watcher.Monitor.QueueRename(previous, destination);
        await watcher.ReconcileAsync(now.AddSeconds(3));
        Assert.Equal(1, await watcher.ReconcileAsync(now.AddSeconds(5)));
        LibraryItem[] items = catalog.Library.List().ToArray();
        Assert.Equal(2, items.Length);
        Assert.Equal(first.SourceId, Assert.Single(items, item => item.SourceLocator == path).SourceId);
        Assert.NotEqual(first.SourceId, Assert.Single(items, item => item.SourceLocator == movedPath).SourceId);
        Assert.Empty(catalog.Sources.ListMovements(first.SourceId));
        Assert.Equal(0L, Count("source_locator_moves"));
        await catalog.Originals.VerifyAsync(first.ItemId);
    }

    [Fact]
    public async Task RenameHistorySurvivesCoherentSnapshotRestore()
    {
        string oldPath = Path.Combine(inbox, "old.md");
        string newPath = Path.Combine(inbox, "moved.md");
        string backup = Path.Combine(workspace, "backup");
        string restoredRoot = Path.Combine(workspace, "restored");
        Guid sourceId;
        Guid itemId;
        using (SqliteCatalog catalog = SqliteCatalog.Open(dataRoot))
        {
            File.WriteAllText(oldPath, "restorable topic");
            IntakeHarness watcher = NewWatcher(catalog);
            DateTimeOffset time = DateTimeOffset.UtcNow;
            await watcher.ReconcileAsync(time);
            await watcher.ReconcileAsync(time.AddSeconds(2));
            LibraryItem captured = Assert.Single(catalog.Library.List());
            sourceId = captured.SourceId;
            itemId = captured.ItemId;

            File.Move(oldPath, newPath);
            watcher.Monitor.QueueRename(oldPath, newPath);
            await watcher.ReconcileAsync(time.AddSeconds(3));
            Assert.Equal(1L, Count("source_locator_moves"));
            await catalog.Recovery.BackupAsync(backup, 8_000_000);
        }

        await CatalogSnapshots.RestoreAsync(backup, restoredRoot, 8_000_000);
        using SqliteCatalog restored = SqliteCatalog.Open(restoredRoot);
        Assert.Equal(20, restored.SchemaVersion);
        LibraryItem recovered = Assert.Single(restored.Library.List());
        Assert.Equal(sourceId, recovered.SourceId);
        Assert.Equal(itemId, recovered.ItemId);
        Assert.Equal(newPath, recovered.SourceLocator);
        await restored.Originals.VerifyAsync(itemId);
        Assert.Equal(1L, Count("source_locator_moves", restored.DatabasePath));
    }

    [Fact]
    public void VersionSevenUpgradeAddsMoveJournalWithoutChangingSourceAuthority()
    {
        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataRoot, "catalog.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString()))
        {
            connection.Open();
            foreach (string name in new[] { "Catalog.V1.sql", "Catalog.V2.sql",
                "Catalog.V3.sql", "Catalog.V4.sql", "Catalog.V5.sql",
                "Catalog.V6.sql", "Catalog.V7.sql" })
            {
                using Stream resource = typeof(SqliteCatalog).Assembly.GetManifestResourceStream(
                    "Praxis.Infrastructure." + name)!;
                using StreamReader reader = new(resource);
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = reader.ReadToEnd();
                command.ExecuteNonQuery();
            }
        }

        using SqliteCatalog upgraded = SqliteCatalog.Open(dataRoot);
        Assert.Equal(20, upgraded.SchemaVersion);
        Assert.Equal(0L, Count("source_locator_moves"));
    }

    private IntakeHarness NewWatcher(SqliteCatalog catalog) =>
        new(catalog, inbox, TimeSpan.FromSeconds(1));

    private long Count(string table, string? databasePath = null)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath ?? Path.Combine(dataRoot, "catalog.sqlite"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
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
