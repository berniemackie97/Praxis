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
        Assert.Empty(catalog.ListLibrary());

        Assert.Equal(2, await watcher.ReconcileAsync(time.AddSeconds(2)));
        var items = catalog.ListLibrary();
        Assert.Equal(2, items.Count);
        var markdown = Assert.Single(items, item => item.SourceLocator == text);
        var unsupported = Assert.Single(items, item => item.SourceLocator == unknown);
        Assert.Equal("completed", markdown.ProcessingState);
        Assert.Equal("completed", markdown.ExtractionOutcome);
        Assert.Equal("unsupported", unsupported.ProcessingState);
        Assert.Equal("unsupported", unsupported.ExtractionOutcome);
        Assert.Equal(markdown.ItemId, Assert.Single(catalog.SearchText("violet")).ItemId);
        await catalog.VerifyOriginalAsync(markdown.ItemId);
        await catalog.VerifyOriginalAsync(unsupported.ItemId);

        Assert.Equal(0, await watcher.ReconcileAsync(time.AddSeconds(3)));
        Assert.Equal(0, await watcher.ReconcileAsync(time.AddMinutes(6)));
        Assert.Equal(2, catalog.ListLibrary().Count);
        Assert.Equal(2L, Count(catalog.DatabasePath, "ingest_requests"));
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
        var first = Assert.Single(catalog.ListLibrary());
        Assert.Single(catalog.SearchText("violet"));

        File.WriteAllText(path, "amber revision");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(5));
        Assert.Equal(0, await watcher.ReconcileAsync(time.AddSeconds(3)));
        Assert.Equal(1, await watcher.ReconcileAsync(time.AddSeconds(5)));
        var second = Assert.Single(catalog.ListLibrary());
        Assert.Equal(first.ItemId, second.ItemId);
        Assert.Equal(first.SourceId, second.SourceId);
        Assert.Equal(2, second.Revision);
        Assert.NotEqual(first.Sha256, second.Sha256);
        Assert.Empty(catalog.SearchText("violet"));
        Assert.Equal(second.ItemId, Assert.Single(catalog.SearchText("amber")).ItemId);

        Assert.Equal(0, await watcher.ReconcileAsync(time.AddMinutes(6)));
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
        Assert.Empty(catalog.ListLibrary());

        File.Move(temporary, completed);
        Assert.Equal(0, await watcher.ReconcileAsync(time.AddSeconds(3)));
        Assert.Equal(1, await watcher.ReconcileAsync(time.AddSeconds(5)));
        Assert.Equal(completed, Assert.Single(catalog.ListLibrary()).SourceLocator);
        Assert.Single(catalog.SearchText("violet"));
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
        var prior = Assert.Single(catalog.ListLibrary());

        File.Delete(path);
        Assert.Equal(0, await watcher.ReconcileAsync(time.AddSeconds(3)));
        Assert.Equal(prior.ItemId, Assert.Single(catalog.ListLibrary()).ItemId);
        Assert.Single(catalog.SearchText("violet"));
        await catalog.VerifyOriginalAsync(prior.ItemId);
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
            before = Assert.Single(catalog.ListLibrary());
        }

        using (var reopened = LocalCatalog.Open(dataRoot))
        {
            var resumed = NewMonitor(reopened);
            await resumed.ReconcileAsync(time.AddSeconds(10));
            Assert.Equal(0, await resumed.ReconcileAsync(time.AddSeconds(12)));
            var after = Assert.Single(reopened.ListLibrary());
            Assert.Equal(before, after);
            Assert.Single(reopened.SearchText("violet"));
            Assert.Equal(1L, Count(reopened.DatabasePath, "ingest_requests"));
            await reopened.VerifyOriginalAsync(after.ItemId);
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
        Assert.Empty(catalog.ListLibrary());
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
            while (catalog.ListLibrary().FirstOrDefault()?.ProcessingState != "completed" &&
                DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            var imported = Assert.Single(catalog.ListLibrary());
            Assert.Equal("completed", imported.ProcessingState);
            Assert.Single(catalog.SearchText("violet"));
        }
        finally
        {
            await cancellation.CancelAsync();
            await running;
        }
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
