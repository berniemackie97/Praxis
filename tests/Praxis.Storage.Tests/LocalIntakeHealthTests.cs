using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class LocalIntakeHealthTests : IDisposable
{
    private readonly string workspace = Path.Combine(Path.GetTempPath(),
        "praxis-intake-health-" + Guid.NewGuid().ToString("N"));
    private readonly string dataRoot;
    private readonly string inbox;

    public LocalIntakeHealthTests()
    {
        dataRoot = Path.Combine(workspace, "data");
        inbox = Path.Combine(workspace, "inbox");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(inbox);
        if (!OperatingSystem.IsWindows())
        {
            var privateMode = UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(workspace, privateMode);
            File.SetUnixFileMode(dataRoot, privateMode);
            File.SetUnixFileMode(inbox, privateMode);
        }
    }

    [Fact]
    public async Task ReconcilePublishesCountsAndClearsResolvedFileErrors()
    {
        var path = Path.Combine(inbox, "note.md");
        File.WriteAllText(path, "# First\nOriginal text");
        using var catalog = LocalCatalog.Open(dataRoot);
        var monitor = new LocalWatchedIntake(catalog, inbox, TimeSpan.Zero);
        var started = monitor.Snapshot();
        Assert.Equal("starting", started.State);
        Assert.Null(started.LastSuccessfulScanUtc);

        var now = DateTimeOffset.UtcNow;
        Assert.Equal(0, await monitor.ReconcileAsync(now));
        Assert.Equal(1, await monitor.ReconcileAsync(now.AddSeconds(1)));
        var captured = Assert.Single(catalog.ListLibrary());
        Assert.Equal("watching", monitor.Snapshot().State);
        Assert.Equal(1, monitor.Snapshot().ObservedFiles);
        Assert.Equal(1, monitor.Snapshot().ImportedSinceStart);
        Assert.Equal(now.AddSeconds(1), monitor.Snapshot().LastSuccessfulScanUtc);

        Assert.True(catalog.RevokeSource(captured.SourceId));
        File.WriteAllText(path, "# Changed\nA revoked revision");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(30));
        Assert.Equal(0, await monitor.ReconcileAsync(now.AddSeconds(2)));
        Assert.Equal(0, await monitor.ReconcileAsync(now.AddSeconds(3)));
        var broken = monitor.Snapshot();
        Assert.Equal("degraded", broken.State);
        Assert.Equal(1, broken.ErrorCount);
        Assert.Single(broken.FileErrors);
        Assert.Equal(path, broken.FileErrors[0].SourcePath);
        Assert.Contains("Revoked", broken.FileErrors[0].Message);
        Assert.Equal(1, broken.ImportedSinceStart);
        await catalog.VerifyOriginalAsync(captured.ItemId);

        File.Delete(path);
        await monitor.ReconcileAsync(now.AddSeconds(4));
        var resumed = monitor.Snapshot();
        Assert.Equal("watching", resumed.State);
        Assert.Equal(0, resumed.ErrorCount);
        Assert.Empty(resumed.FileErrors);
        Assert.Equal(0, resumed.ObservedFiles);
        Assert.Single(catalog.ListLibrary());
    }

    [Fact]
    public async Task OwnerPausePreventsNewCapturesAndResumeReconcilesThem()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var monitor = new LocalWatchedIntake(catalog, inbox,
            TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        Assert.True(monitor.SetPaused(true).Paused);
        using var cancellation = new CancellationTokenSource();
        var running = monitor.RunAsync(cancellation.Token);
        try
        {
            var first = Path.Combine(inbox, "paused.md");
            File.WriteAllText(first, "Written while paused");
            await Task.Delay(350);
            Assert.Empty(catalog.ListLibrary());
            Assert.Equal("paused", monitor.Snapshot().State);

            Assert.False(monitor.SetPaused(false).Paused);
            await WaitForAsync(() =>
                catalog.ListLibrary().FirstOrDefault()?.ProcessingState == "completed");
            Assert.Equal("watching", monitor.Snapshot().State);
            Assert.Equal(1, monitor.Snapshot().ImportedSinceStart);
            Assert.Single(catalog.SearchText("paused"));

            monitor.SetPaused(true);
            File.WriteAllText(first, "Completely different revision");
            File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(1));
            await Task.Delay(350);
            Assert.Equal(1, Assert.Single(catalog.ListLibrary()).Revision);

            monitor.SetPaused(false);
            await WaitForAsync(() =>
                catalog.ListLibrary().FirstOrDefault()?.Revision == 2);
            Assert.Equal(2, monitor.Snapshot().ImportedSinceStart);
            Assert.Empty(catalog.SearchText("paused"));
            await catalog.VerifyOriginalAsync(Assert.Single(catalog.ListLibrary()).ItemId);
        }
        finally
        {
            await cancellation.CancelAsync();
            await running;
        }

        Assert.Equal("stopped", monitor.Snapshot().State);
    }

    [Fact]
    public async Task OfflineWatcherReportsDisconnectionAndRecoversWithoutRecreatingOriginals()
    {
        var removable = Path.Combine(workspace, "removable");
        using var catalog = LocalCatalog.Open(dataRoot);
        var monitor = new LocalWatchedIntake(catalog, removable,
            TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        using var cancellation = new CancellationTokenSource();
        var running = monitor.RunAsync(cancellation.Token);
        try
        {
            await WaitForAsync(() => monitor.Snapshot().State == "offline");
            Assert.Null(monitor.Snapshot().LastSuccessfulScanUtc);
            Assert.Contains("Reconnect", monitor.Snapshot().Problem);

            Directory.CreateDirectory(removable);
            File.WriteAllText(Path.Combine(removable, "reconnected.md"), "# Reconnected");
            await WaitForAsync(() =>
                catalog.ListLibrary().FirstOrDefault()?.ProcessingState == "completed");
            var saved = Assert.Single(catalog.ListLibrary());
            Assert.Equal("watching", monitor.Snapshot().State);
            Assert.NotNull(monitor.Snapshot().LastSuccessfulScanUtc);
            Assert.Single(catalog.SearchText("Reconnected"));

            Directory.Delete(removable, recursive: true);
            await WaitForAsync(() => monitor.Snapshot().State == "offline");
            await catalog.VerifyOriginalAsync(saved.ItemId);
            Assert.Single(catalog.ListLibrary());
            Assert.Single(catalog.SearchText("Reconnected"));
        }
        finally
        {
            await cancellation.CancelAsync();
            await running;
        }
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        var timeout = DateTime.UtcNow.AddSeconds(10);
        while (!predicate() && DateTime.UtcNow < timeout)
        {
            await Task.Delay(50);
        }

        Assert.True(predicate(), "The intake worker did not reach its expected state.");
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
