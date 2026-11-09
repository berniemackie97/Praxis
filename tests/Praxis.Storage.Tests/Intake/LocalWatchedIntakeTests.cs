using Microsoft.Data.Sqlite;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class LocalWatchedIntakeTests : IDisposable
{
    private readonly string workspace = Path.Combine(
        Path.GetTempPath(), "praxis-watched-" + Guid.NewGuid().ToString("N"));
    private readonly string dataRoot;
    private readonly string inbox;

    public LocalWatchedIntakeTests()
    {
        dataRoot = Path.Combine(workspace, "data");
        inbox = Path.Combine(workspace, "inbox");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(inbox);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dataRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task StableDropsEnterDurableLibraryAndSupportedTextIsSearchable()
    {
        var text = Path.Combine(inbox, "plan.md");
        var unknown = Path.Combine(inbox, "archive.data");
        File.WriteAllText(text, "# Plan\nThe violet notebook is ready.");
        File.WriteAllBytes(unknown, [0, 1, 255, 2]);

        using var catalog = LocalCatalog.Open(dataRoot);
        var watcher = NewMonitor(catalog);
        var time = DateTimeOffset.UtcNow;
        Assert.Equal(0, await watcher.ReconcileAsync(time));
        Assert.Empty(catalog.Library.List());

        Assert.Equal(2, await watcher.ReconcileAsync(time.AddSeconds(2)));
        var items = catalog.Library.List();
        Assert.Equal(2, items.Count);
        var markdown = Assert.Single(items, item => item.SourceLocator == text);
        var unsupported = Assert.Single(items, item => item.SourceLocator == unknown);
        Assert.Equal("completed", markdown.ProcessingState);
        Assert.Equal("completed", markdown.ExtractionOutcome);
        Assert.Equal("unsupported", unsupported.ProcessingState);
        Assert.Equal("unsupported", unsupported.ExtractionOutcome);
        Assert.Equal(markdown.ItemId, Assert.Single(catalog.Search.Query("violet")).ItemId);
        await catalog.Originals.VerifyAsync(markdown.ItemId);
        await catalog.Originals.VerifyAsync(unsupported.ItemId);

        Assert.Equal(0, await watcher.ReconcileAsync(time.AddSeconds(3)));
        Assert.Equal(0, await watcher.ReconcileAsync(time.AddMinutes(31)));
        Assert.Equal(2, catalog.Library.List().Count);
        Assert.Equal(2L, Count(catalog.DatabasePath, "ingest_requests"));
    }

    [Fact]
    public async Task TechnicalFilesDroppedIntoInboxAreIndexedAndRemainAvailableAfterRestart()
    {
        var codePath = Path.Combine(inbox, "C3Reader.cs");
        var dumpPath = Path.Combine(inbox, "C3Dump.asm");
        var opaquePath = Path.Combine(inbox, "packed.bin");
        File.WriteAllText(codePath, "public sealed class C3ChunkReader {}");
        File.WriteAllText(dumpPath, "C3ChunkReader PROC\\nRET\\nC3ChunkReader ENDP");
        File.WriteAllBytes(opaquePath, [0, 255, 1]);

        LocalSearchHit[] indexed;
        var time = DateTimeOffset.UtcNow;
        using (var catalog = LocalCatalog.Open(dataRoot))
        {
            var watcher = NewMonitor(catalog);
            Assert.Equal(0, await watcher.ReconcileAsync(time));
            Assert.Equal(3, await watcher.ReconcileAsync(time.AddSeconds(2)));
            indexed = catalog.Search.Query("C3ChunkReader").ToArray();
            Assert.Equal(2, indexed.Length);
            Assert.All(indexed, hit => Assert.Equal(1, hit.Revision));
            Assert.Equal("unsupported", catalog.Library.List().Single(
                item => item.SourceLocator == opaquePath).ExtractionOutcome);
            foreach (var hit in indexed)
            {
                await catalog.Originals.VerifyAsync(hit.ItemId);
            }
        }

        using var reopened = LocalCatalog.Open(dataRoot);
        var restored = reopened.Search.Query("C3ChunkReader");
        Assert.Equal(indexed.Select(hit => hit.SourceId).OrderBy(id => id),
            restored.Select(hit => hit.SourceId).OrderBy(id => id));
        Assert.Equal(3, reopened.Library.List().Count);
    }

    [Fact]
    public async Task ContentUpdatesPreserveSourceIdentityAndInvalidatePreviousSearch()
    {
        var path = Path.Combine(inbox, "changing.txt");
        File.WriteAllText(path, "violet revision");
        using var catalog = LocalCatalog.Open(dataRoot);
        var watcher = NewMonitor(catalog);
        var time = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(time);
        Assert.Equal(1, await watcher.ReconcileAsync(time.AddSeconds(2)));
        var first = Assert.Single(catalog.Library.List());
        Assert.Single(catalog.Search.Query("violet"));

        File.WriteAllText(path, "amber revision");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(0, await watcher.ReconcileAsync(time.AddSeconds(3)));
        Assert.Equal(1, await watcher.ReconcileAsync(time.AddSeconds(5)));
        var second = Assert.Single(catalog.Library.List());
        Assert.Equal(first.ItemId, second.ItemId);
        Assert.Equal(first.SourceId, second.SourceId);
        Assert.Equal(2, second.Revision);
        Assert.NotEqual(first.Sha256, second.Sha256);
        Assert.Empty(catalog.Search.Query("violet"));
        Assert.Equal(second.ItemId, Assert.Single(catalog.Search.Query("amber")).ItemId);

        Assert.Equal(0, await watcher.ReconcileAsync(time.AddMinutes(31)));
        Assert.Equal(2L, Count(catalog.DatabasePath, "item_versions"));
    }

    [Fact]
    public async Task TemporaryFilesAreIgnoredAndAtomicRenameIsCaptured()
    {
        var temporary = Path.Combine(inbox, "download.part");
        var completed = Path.Combine(inbox, "download.md");
        File.WriteAllText(temporary, "a temporary violet note");
        using var catalog = LocalCatalog.Open(dataRoot);
        var watcher = NewMonitor(catalog);
        var time = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(time);
        Assert.Equal(0, await watcher.ReconcileAsync(time.AddSeconds(2)));
        Assert.Empty(catalog.Library.List());

        File.Move(temporary, completed);
        Assert.Equal(0, await watcher.ReconcileAsync(time.AddSeconds(3)));
        Assert.Equal(1, await watcher.ReconcileAsync(time.AddSeconds(5)));
        Assert.Equal(completed, Assert.Single(catalog.Library.List()).SourceLocator);
        Assert.Single(catalog.Search.Query("violet"));
    }

    [Fact]
    public async Task DeletingAWatchedFileDoesNotRevokeItsPreservedOriginal()
    {
        var path = Path.Combine(inbox, "history.txt");
        File.WriteAllText(path, "original violet history");
        using var catalog = LocalCatalog.Open(dataRoot);
        var watcher = NewMonitor(catalog);
        var time = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(time);
        await watcher.ReconcileAsync(time.AddSeconds(2));
        var prior = Assert.Single(catalog.Library.List());

        File.Delete(path);
        Assert.Equal(0, await watcher.ReconcileAsync(time.AddSeconds(3)));
        Assert.Equal(prior.ItemId, Assert.Single(catalog.Library.List()).ItemId);
        Assert.Single(catalog.Search.Query("violet"));
        await catalog.Originals.VerifyAsync(prior.ItemId);
    }

    [Fact]
    public async Task ReconciliationAfterRestartKeepsCommittedVersionAndReceipt()
    {
        var path = Path.Combine(inbox, "restart.md");
        File.WriteAllText(path, "# violet durable");
        LocalLibraryItem before;
        var time = DateTimeOffset.UtcNow;
        using (var catalog = LocalCatalog.Open(dataRoot))
        {
            var first = NewMonitor(catalog);
            await first.ReconcileAsync(time);
            await first.ReconcileAsync(time.AddSeconds(2));
            before = Assert.Single(catalog.Library.List());
        }

        using (var reopened = LocalCatalog.Open(dataRoot))
        {
            var resumed = NewMonitor(reopened);
            await resumed.ReconcileAsync(time.AddSeconds(10));
            Assert.Equal(0, await resumed.ReconcileAsync(time.AddSeconds(12)));
            var after = Assert.Single(reopened.Library.List());
            Assert.Equal(before, after);
            Assert.Single(reopened.Search.Query("violet"));
            Assert.Equal(1L, Count(reopened.DatabasePath, "ingest_requests"));
            await reopened.Originals.VerifyAsync(after.ItemId);
        }
    }

    [Fact]
    public async Task RejectedLinksPreventImportOutsideApprovedDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var outside = Path.Combine(workspace, "outside.txt");
        File.WriteAllText(outside, "private");
        File.CreateSymbolicLink(Path.Combine(inbox, "linked.txt"), outside);
        using var catalog = LocalCatalog.Open(dataRoot);
        var watcher = NewMonitor(catalog);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            watcher.ReconcileAsync(DateTimeOffset.UtcNow));
        Assert.Empty(catalog.Library.List());
        Assert.Equal("private", File.ReadAllText(outside));
    }

    [Fact]
    public async Task ActiveMonitorNoticesNewDropsWithoutAnImportCommand()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var monitor = NewMonitor(catalog, TimeSpan.Zero,
            TimeSpan.FromMilliseconds(100));
        using var cancellation = new CancellationTokenSource();
        var running = monitor.RunAsync(cancellation.Token);
        try
        {
            await Task.Delay(200);
            File.WriteAllText(Path.Combine(inbox, "live.txt"), "violet observation");
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (catalog.Library.List().FirstOrDefault()?.ProcessingState != "completed" &&
                DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            var imported = Assert.Single(catalog.Library.List());
            Assert.Equal("completed", imported.ProcessingState);
            Assert.Single(catalog.Search.Query("violet"));
        }
        finally
        {
            await cancellation.CancelAsync();
            await running;
        }
    }

    [Fact]
    public async Task OfflineApprovedIntakeResumesAfterDirectoryReturns()
    {
        var removable = Path.Combine(workspace, "offline");
        using var catalog = LocalCatalog.Open(dataRoot);
        var monitor = new LocalWatchedIntake(catalog, removable,
            TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        using var cancellation = new CancellationTokenSource();
        var running = monitor.RunAsync(cancellation.Token);
        try
        {
            await Task.Delay(200);
            Assert.False(Directory.Exists(removable));
            Directory.CreateDirectory(removable);
            File.WriteAllText(Path.Combine(removable, "resumed.md"), "violet reconnected");
            await WaitForAsync(() =>
                catalog.Library.List().FirstOrDefault()?.ProcessingState == "completed");
            Assert.Single(catalog.Search.Query("reconnected"));
        }
        finally
        {
            await cancellation.CancelAsync();
            await running;
        }
    }

    [Fact]
    public async Task OfflineWatcherStillDrainsPreviouslyQueuedProcessing()
    {
        var outside = Path.Combine(workspace, "queued.txt");
        File.WriteAllText(outside, "violet from existing queue");
        using var catalog = LocalCatalog.Open(dataRoot);
        var original = await new LocalSourceIntake(catalog)
            .ImportFileAsync(outside, 100, "offline-queued");
        Assert.Equal("queued", Assert.Single(catalog.Processing.List()).State);

        var monitor = new LocalWatchedIntake(
            catalog, Path.Combine(workspace, "not-mounted"),
            TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        using var cancellation = new CancellationTokenSource();
        var running = monitor.RunAsync(cancellation.Token);
        try
        {
            await WaitForAsync(() =>
                catalog.Text.Read(original.ItemId, 1)?.Outcome == "completed");
            Assert.Equal(original.ItemId, Assert.Single(catalog.Search.Query("violet")).ItemId);
        }
        finally
        {
            await cancellation.CancelAsync();
            await running;
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.True(condition(), "The local intake worker did not converge.");
    }

    private LocalWatchedIntake NewMonitor(
        LocalCatalog catalog, TimeSpan? quietPeriod = null, TimeSpan? scanInterval = null) =>
        new(catalog, inbox, quietPeriod ?? TimeSpan.FromSeconds(1),
            scanInterval ?? TimeSpan.FromSeconds(10));

    private static long Count(string databasePath, string table)
    {
        using var database = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        database.Open();
        using var command = database.CreateCommand();
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
