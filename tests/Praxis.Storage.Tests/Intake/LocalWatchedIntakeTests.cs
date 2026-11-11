using Microsoft.Data.Sqlite;
using Praxis.Storage.Catalog;
using Praxis.Storage.Intake;
using Praxis.Storage.Library;
using Praxis.Storage.Originals;
using Praxis.Storage.Search;
using Praxis.Storage.Tests.Support;
using Xunit;

namespace Praxis.Storage.Tests.Intake;

public sealed class LocalWatchedIntakeTests : IDisposable
{
    private readonly string workspace = Path.Combine(
        TestPaths.Temp, "praxis-watched-" + Guid.NewGuid().ToString("N"));
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
        string text = Path.Combine(inbox, "plan.md");
        string unknown = Path.Combine(inbox, "archive.data");
        File.WriteAllText(text, "# Plan\nThe violet notebook is ready.");
        File.WriteAllBytes(unknown, [0, 1, 255, 2]);

        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        IntakeHarness watcher = NewMonitor(catalog);
        DateTimeOffset time = DateTimeOffset.UtcNow;
        Assert.Equal(0, await watcher.ReconcileAsync(time));
        Assert.Empty(catalog.Library.List());

        Assert.Equal(2, await watcher.ReconcileAsync(time.AddSeconds(2)));
        IReadOnlyList<LocalLibraryItem> items = catalog.Library.List();
        Assert.Equal(2, items.Count);
        LocalLibraryItem markdown = Assert.Single(items, item => item.SourceLocator == text);
        LocalLibraryItem unsupported = Assert.Single(items, item => item.SourceLocator == unknown);
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
        string codePath = Path.Combine(inbox, "C3Reader.cs");
        string dumpPath = Path.Combine(inbox, "C3Dump.asm");
        string opaquePath = Path.Combine(inbox, "packed.bin");
        File.WriteAllText(codePath, "public sealed class C3ChunkReader {}");
        File.WriteAllText(dumpPath, "C3ChunkReader PROC\\nRET\\nC3ChunkReader ENDP");
        File.WriteAllBytes(opaquePath, [0, 255, 1]);

        LocalSearchHit[] indexed;
        DateTimeOffset time = DateTimeOffset.UtcNow;
        using (LocalCatalog catalog = LocalCatalog.Open(dataRoot))
        {
            IntakeHarness watcher = NewMonitor(catalog);
            Assert.Equal(0, await watcher.ReconcileAsync(time));
            Assert.Equal(3, await watcher.ReconcileAsync(time.AddSeconds(2)));
            indexed = catalog.Search.Query("C3ChunkReader").ToArray();
            Assert.Equal(2, indexed.Length);
            Assert.All(indexed, hit => Assert.Equal(1, hit.Revision));
            Assert.Equal("unsupported", catalog.Library.List().Single(
                item => item.SourceLocator == opaquePath).ExtractionOutcome);
            foreach (LocalSearchHit hit in indexed)
            {
                await catalog.Originals.VerifyAsync(hit.ItemId);
            }
        }

        using LocalCatalog reopened = LocalCatalog.Open(dataRoot);
        IReadOnlyList<LocalSearchHit> restored = reopened.Search.Query("C3ChunkReader");
        Assert.Equal(indexed.Select(hit => hit.SourceId).OrderBy(id => id),
            restored.Select(hit => hit.SourceId).OrderBy(id => id));
        Assert.Equal(3, reopened.Library.List().Count);
    }

    [Fact]
    public async Task ContentUpdatesPreserveSourceIdentityAndInvalidatePreviousSearch()
    {
        string path = Path.Combine(inbox, "changing.txt");
        File.WriteAllText(path, "violet revision");
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        IntakeHarness watcher = NewMonitor(catalog);
        DateTimeOffset time = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(time);
        Assert.Equal(1, await watcher.ReconcileAsync(time.AddSeconds(2)));
        LocalLibraryItem first = Assert.Single(catalog.Library.List());
        Assert.Single(catalog.Search.Query("violet"));

        File.WriteAllText(path, "amber revision");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(0, await watcher.ReconcileAsync(time.AddSeconds(3)));
        Assert.Equal(1, await watcher.ReconcileAsync(time.AddSeconds(5)));
        LocalLibraryItem second = Assert.Single(catalog.Library.List());
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
        string temporary = Path.Combine(inbox, "download.part");
        string completed = Path.Combine(inbox, "download.md");
        File.WriteAllText(temporary, "a temporary violet note");
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        IntakeHarness watcher = NewMonitor(catalog);
        DateTimeOffset time = DateTimeOffset.UtcNow;
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
        string path = Path.Combine(inbox, "history.txt");
        File.WriteAllText(path, "original violet history");
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        IntakeHarness watcher = NewMonitor(catalog);
        DateTimeOffset time = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(time);
        await watcher.ReconcileAsync(time.AddSeconds(2));
        LocalLibraryItem prior = Assert.Single(catalog.Library.List());

        File.Delete(path);
        Assert.Equal(0, await watcher.ReconcileAsync(time.AddSeconds(3)));
        Assert.Equal(prior.ItemId, Assert.Single(catalog.Library.List()).ItemId);
        Assert.Single(catalog.Search.Query("violet"));
        await catalog.Originals.VerifyAsync(prior.ItemId);
    }

    [Fact]
    public async Task ReconciliationAfterRestartKeepsCommittedVersionAndReceipt()
    {
        string path = Path.Combine(inbox, "restart.md");
        File.WriteAllText(path, "# violet durable");
        LocalLibraryItem before;
        DateTimeOffset time = DateTimeOffset.UtcNow;
        using (LocalCatalog catalog = LocalCatalog.Open(dataRoot))
        {
            IntakeHarness first = NewMonitor(catalog);
            await first.ReconcileAsync(time);
            await first.ReconcileAsync(time.AddSeconds(2));
            before = Assert.Single(catalog.Library.List());
        }

        using (LocalCatalog reopened = LocalCatalog.Open(dataRoot))
        {
            IntakeHarness resumed = NewMonitor(reopened);
            await resumed.ReconcileAsync(time.AddSeconds(10));
            Assert.Equal(0, await resumed.ReconcileAsync(time.AddSeconds(12)));
            LocalLibraryItem after = Assert.Single(reopened.Library.List());
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

        string outside = Path.Combine(workspace, "outside.txt");
        File.WriteAllText(outside, "private");
        File.CreateSymbolicLink(Path.Combine(inbox, "linked.txt"), outside);
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        IntakeHarness watcher = NewMonitor(catalog);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            watcher.ReconcileAsync(DateTimeOffset.UtcNow));
        Assert.Empty(catalog.Library.List());
        Assert.Equal("private", File.ReadAllText(outside));
    }

    [Fact]
    public async Task ActiveMonitorNoticesNewDropsWithoutAnImportCommand()
    {
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        IntakeHarness monitor = NewMonitor(catalog, TimeSpan.Zero,
            TimeSpan.FromMilliseconds(100));
        using CancellationTokenSource cancellation = new();
        Task running = monitor.RunAsync(cancellation.Token);
        try
        {
            await Task.Delay(200);
            File.WriteAllText(Path.Combine(inbox, "live.txt"), "violet observation");
            DateTime deadline = DateTime.UtcNow.AddSeconds(10);
            while (catalog.Library.List().FirstOrDefault()?.ProcessingState != "completed" &&
                DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            LocalLibraryItem imported = Assert.Single(catalog.Library.List());
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
        string removable = Path.Combine(workspace, "offline");
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        IntakeHarness monitor = new(catalog, removable,
            TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        using CancellationTokenSource cancellation = new();
        Task running = monitor.RunAsync(cancellation.Token);
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
        string outside = Path.Combine(workspace, "queued.txt");
        File.WriteAllText(outside, "violet from existing queue");
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        CapturedOriginal original = await new LocalSourceIntake(catalog)
            .ImportFileAsync(outside, 100, "offline-queued");
        Assert.Equal("queued", Assert.Single(catalog.Processing.List()).State);

        IntakeHarness monitor = new(
            catalog, Path.Combine(workspace, "not-mounted"),
            TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        using CancellationTokenSource cancellation = new();
        Task running = monitor.RunAsync(cancellation.Token);
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


    [Fact]
    public async Task ChangedNotificationRehashesWhenSizeAndTimestampAreUnchanged()
    {
        string path = Path.Combine(inbox, "metadata.md");
        DateTime fixedWrite = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(path, "violet contents");
        File.SetLastWriteTimeUtc(path, fixedWrite);
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        IntakeHarness watcher = NewMonitor(catalog);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(now);
        Assert.Equal(1, await watcher.ReconcileAsync(now.AddSeconds(2)));
        LocalLibraryItem before = Assert.Single(catalog.Library.List());

        File.WriteAllText(path, "orange contents");
        File.SetLastWriteTimeUtc(path, fixedWrite);
        Assert.Equal(0, await watcher.ReconcileAsync(now.AddSeconds(3)));
        watcher.Monitor.QueueChanged(path);
        Assert.Equal(0, await watcher.ReconcileAsync(now.AddSeconds(4)));
        Assert.Equal(1, await watcher.ReconcileAsync(now.AddSeconds(6)));

        LocalLibraryItem after = Assert.Single(catalog.Library.List());
        Assert.Equal(before.ItemId, after.ItemId);
        Assert.Equal(before.SourceId, after.SourceId);
        Assert.Equal(2, after.Revision);
        Assert.Equal(2L, Count(catalog.DatabasePath, "ingest_requests"));
        Assert.Empty(catalog.Search.Query("violet"));
        Assert.Single(catalog.Search.Query("orange"));
        await catalog.Originals.VerifyAsync(after.ItemId);
    }

    [Fact]
    public async Task WatcherErrorForcesFullRecheckEvenWithUnchangedMetadata()
    {
        string path = Path.Combine(inbox, "overflow.md");
        DateTime fixedWrite = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(path, "violet contents");
        File.SetLastWriteTimeUtc(path, fixedWrite);
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        IntakeHarness watcher = NewMonitor(catalog);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(now);
        Assert.Equal(1, await watcher.ReconcileAsync(now.AddSeconds(2)));

        File.WriteAllText(path, "orange contents");
        File.SetLastWriteTimeUtc(path, fixedWrite);
        Assert.Equal(0, await watcher.ReconcileAsync(now.AddSeconds(3)));
        watcher.Monitor.NotifyWatcherError();
        Assert.Equal(0, await watcher.ReconcileAsync(now.AddSeconds(4)));
        Assert.Equal(1, await watcher.ReconcileAsync(now.AddSeconds(6)));

        LocalLibraryItem latest = Assert.Single(catalog.Library.List());
        Assert.Equal(2, latest.Revision);
        Assert.Empty(catalog.Search.Query("violet"));
        Assert.Single(catalog.Search.Query("orange"));
        await catalog.Originals.VerifyAsync(latest.ItemId);
    }

    [Fact]
    public async Task ExplicitScanHonorsOwnerPauseWithoutCreatingReceipts()
    {
        string path = Path.Combine(inbox, "paused.txt");
        File.WriteAllText(path, "violet pause gate");
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        IntakeHarness watcher = NewMonitor(catalog);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Assert.True(watcher.SetPaused(true).Paused);
        await watcher.ReconcileAsync(now);
        await watcher.ReconcileAsync(now.AddSeconds(5));
        Assert.Empty(catalog.Library.List());
        Assert.Equal("paused", watcher.Snapshot().State);

        Assert.False(watcher.SetPaused(false).Paused);
        Assert.Equal(0, await watcher.ReconcileAsync(now.AddSeconds(6)));
        Assert.Equal(1, await watcher.ReconcileAsync(now.AddSeconds(8)));
        Assert.Single(catalog.Search.Query("violet"));
        Assert.Equal(1L, Count(catalog.DatabasePath, "ingest_requests"));
    }

    [Fact]
    public async Task AtomicReplacementCreatesOneRevisionAndRetractsOldSearch()
    {
        string path = Path.Combine(inbox, "atomic.md");
        string staging = Path.Combine(inbox, "atomic.part");
        File.WriteAllText(path, "violet preserved version");
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        IntakeHarness watcher = NewMonitor(catalog);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(now);
        Assert.Equal(1, await watcher.ReconcileAsync(now.AddSeconds(2)));
        LocalLibraryItem before = Assert.Single(catalog.Library.List());

        File.WriteAllText(staging, "orange replacement version");
        Assert.Equal(0, await watcher.ReconcileAsync(now.AddSeconds(3)));
        File.Move(staging, path, overwrite: true);
        Assert.Equal(0, await watcher.ReconcileAsync(now.AddSeconds(4)));
        Assert.Equal(1, await watcher.ReconcileAsync(now.AddSeconds(6)));

        LocalLibraryItem after = Assert.Single(catalog.Library.List());
        Assert.Equal(before.ItemId, after.ItemId);
        Assert.Equal(before.SourceId, after.SourceId);
        Assert.Equal(2, after.Revision);
        Assert.Equal(2L, Count(catalog.DatabasePath, "ingest_requests"));
        Assert.Empty(catalog.Search.Query("violet"));
        Assert.Single(catalog.Search.Query("orange"));
        await catalog.Originals.VerifyAsync(after.ItemId);
    }


    [Fact]
    public async Task ResumeRechecksModifiedBytesWhenMetadataIsUnchanged()
    {
        string path = Path.Combine(inbox, "paused-revision.md");
        DateTime fixedWrite = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(path, "violet contents");
        File.SetLastWriteTimeUtc(path, fixedWrite);
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        IntakeHarness watcher = NewMonitor(catalog);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(now);
        await watcher.ReconcileAsync(now.AddSeconds(2));
        LocalLibraryItem before = Assert.Single(catalog.Library.List());

        watcher.SetPaused(true);
        File.WriteAllText(path, "orange contents");
        File.SetLastWriteTimeUtc(path, fixedWrite);
        Assert.Equal(0, await watcher.ReconcileAsync(now.AddSeconds(3)));
        watcher.SetPaused(false);
        Assert.Equal(0, await watcher.ReconcileAsync(now.AddSeconds(4)));
        Assert.Equal(1, await watcher.ReconcileAsync(now.AddSeconds(6)));
        LocalLibraryItem after = Assert.Single(catalog.Library.List());
        Assert.Equal(before.SourceId, after.SourceId);
        Assert.Equal(before.ItemId, after.ItemId);
        Assert.Equal(2, after.Revision);
        Assert.Empty(catalog.Search.Query("violet"));
        Assert.Single(catalog.Search.Query("orange"));
    }


    [Fact]
    public async Task NotificationBacklogOverflowForcesBoundedFullRecheck()
    {
        string path = Path.Combine(inbox, "coalesced.md");
        DateTime fixedWrite = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.WriteAllText(path, "violet contents");
        File.SetLastWriteTimeUtc(path, fixedWrite);
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        IntakeHarness watcher = NewMonitor(catalog);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await watcher.ReconcileAsync(now);
        Assert.Equal(1, await watcher.ReconcileAsync(now.AddSeconds(2)));

        File.WriteAllText(path, "orange contents");
        File.SetLastWriteTimeUtc(path, fixedWrite);
        for (int index = 0; index < 513; index++)
        {
            watcher.Monitor.QueueChanged(Path.Combine(inbox, $"changed-{index}.md"));
        }

        Assert.Equal(0, await watcher.ReconcileAsync(now.AddSeconds(3)));
        Assert.Equal(1, await watcher.ReconcileAsync(now.AddSeconds(5)));
        LocalLibraryItem latest = Assert.Single(catalog.Library.List());
        Assert.Equal(2, latest.Revision);
        Assert.Empty(catalog.Search.Query("violet"));
        Assert.Single(catalog.Search.Query("orange"));
        await catalog.Originals.VerifyAsync(latest.ItemId);
    }

    [Fact]
    public async Task OversizedSparseFileReportsAnErrorAndRecoversAfterReplacement()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string path = Path.Combine(inbox, "large.txt");
        using (FileStream file = new(path, FileMode.CreateNew, FileAccess.Write))
        {
            file.SetLength(2L * 1024 * 1024 * 1024 + 1);
        }

        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        IntakeHarness watcher = NewMonitor(catalog);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Equal(0, await watcher.ReconcileAsync(now));
        Assert.Empty(catalog.Library.List());
        LocalIntakeStatus rejected = watcher.Snapshot();
        Assert.Equal("degraded", rejected.State);
        Assert.Equal(1, rejected.ErrorCount);
        Assert.Contains("byte limit", Assert.Single(rejected.FileErrors).Message);

        File.Delete(path);
        File.WriteAllText(path, "violet recovered");
        Assert.Equal(0, await watcher.ReconcileAsync(now.AddSeconds(1)));
        Assert.Equal(1, await watcher.ReconcileAsync(now.AddSeconds(3)));
        Assert.Equal("watching", watcher.Snapshot().State);
        Assert.Empty(watcher.Snapshot().FileErrors);
        Assert.Equal("completed", Assert.Single(catalog.Library.List()).ExtractionOutcome);
        Assert.Single(catalog.Search.Query("violet"));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.True(condition(), "The local intake worker did not converge.");
    }

    private IntakeHarness NewMonitor(
        LocalCatalog catalog, TimeSpan? quietPeriod = null, TimeSpan? scanInterval = null) =>
        new(catalog, inbox, quietPeriod ?? TimeSpan.FromSeconds(1),
            scanInterval ?? TimeSpan.FromSeconds(10));

    private static long Count(string databasePath, string table)
    {
        using SqliteConnection database = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        database.Open();
        using SqliteCommand command = database.CreateCommand();
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
