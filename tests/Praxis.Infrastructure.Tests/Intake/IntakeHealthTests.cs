using System.Text.Json;
using Praxis.Application.Desktop;
using Praxis.Domain.Intake;
using Praxis.Domain.Library;
using Praxis.Domain.Originals;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Intake;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Intake;

public sealed class IntakeHealthTests : IDisposable
{
    private readonly string workspace = Path.Combine(TestPaths.Temp,
        "praxis-intake-health-" + Guid.NewGuid().ToString("N"));
    private readonly string dataRoot;
    private readonly string inbox;

    public IntakeHealthTests()
    {
        dataRoot = Path.Combine(workspace, "data");
        inbox = Path.Combine(workspace, "inbox");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(inbox);
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode privateMode = UnixFileMode.UserRead |
                                       UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(workspace, privateMode);
            File.SetUnixFileMode(dataRoot, privateMode);
            File.SetUnixFileMode(inbox, privateMode);
        }
    }

    [Fact]
    public async Task WatcherFaultLeavesPollingAbleToCaptureStableFiles()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        WatchedFolderIntake monitor = new(catalog.Sources, catalog.Originals, dataRoot, inbox,
            quietPeriod: TimeSpan.FromMilliseconds(300), rescanInterval: TimeSpan.FromMilliseconds(100));
        using CancellationTokenSource cancellation = new();
        Task running = monitor.RunAsync(cancellation.Token);
        try
        {
            await WaitForAsync(() => monitor.Snapshot().LastSuccessfulScanUtc is not null);
            monitor.NotifyWatcherError();
            File.WriteAllText(Path.Combine(inbox, "fallback.md"), "Capture this while notifications recover.");
            await WaitForAsync(() => monitor.Snapshot().ImportedSinceStart == 1);
            Assert.Single(catalog.Library.List());
            Assert.Equal(0, monitor.Snapshot().ErrorCount);
        }
        finally
        {
            await cancellation.CancelAsync();
            await running;
        }
    }

    [Fact]
    public async Task RemovingAnOversizedFileClearsItsErrorWithoutAnObservation()
    {
        string path = Path.Combine(inbox, "oversized.bin");
        using (FileStream file = File.Create(path))
        {
            file.SetLength(2L * 1024 * 1024 * 1024 + 1);
        }
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        IntakeHarness monitor = new(catalog, inbox, TimeSpan.Zero);
        await monitor.ReconcileAsync(DateTimeOffset.UtcNow);
        Assert.Equal(1, monitor.Snapshot().ErrorCount);
        File.Delete(path);
        await monitor.ReconcileAsync(DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal("watching", monitor.Snapshot().State);
        Assert.Equal(0, monitor.Snapshot().ErrorCount);
    }

    [Fact]
    public async Task ReconcilePublishesCountsAndClearsResolvedFileErrors()
    {
        string path = Path.Combine(inbox, "note.md");
        File.WriteAllText(path, "# First\nOriginal text");
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        IntakeHarness monitor = new(catalog, inbox, TimeSpan.Zero);
        IntakeStatus started = monitor.Snapshot();
        Assert.Equal("starting", started.State);
        Assert.Null(started.LastSuccessfulScanUtc);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Equal(0, await monitor.ReconcileAsync(now));
        Assert.Equal(1, await monitor.ReconcileAsync(now.AddSeconds(1)));
        LibraryItem captured = Assert.Single(catalog.Library.List());
        Assert.Equal("watching", monitor.Snapshot().State);
        Assert.Equal(1, monitor.Snapshot().ObservedFiles);
        Assert.Equal(1, monitor.Snapshot().ImportedSinceStart);
        Assert.Equal(now.AddSeconds(1), monitor.Snapshot().LastSuccessfulScanUtc);

        Assert.True(catalog.Sources.Revoke(captured.SourceId));
        File.WriteAllText(path, "# Changed\nA revoked revision");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(30));
        Assert.Equal(0, await monitor.ReconcileAsync(now.AddSeconds(2)));
        Assert.Equal(0, await monitor.ReconcileAsync(now.AddSeconds(3)));
        IntakeStatus broken = monitor.Snapshot();
        Assert.Equal("degraded", broken.State);
        Assert.Equal(1, broken.ErrorCount);
        Assert.Single(broken.FileErrors);
        Assert.Equal(path, broken.FileErrors[0].SourcePath);
        Assert.Contains("Revoked", broken.FileErrors[0].Message);
        Assert.Equal(1, broken.ImportedSinceStart);
        await catalog.Originals.VerifyAsync(captured.ItemId);

        File.Delete(path);
        await monitor.ReconcileAsync(now.AddSeconds(4));
        IntakeStatus resumed = monitor.Snapshot();
        Assert.Equal("watching", resumed.State);
        Assert.Equal(0, resumed.ErrorCount);
        Assert.Empty(resumed.FileErrors);
        Assert.Equal(0, resumed.ObservedFiles);
        Assert.Single(catalog.Library.List());
    }

    [Fact]
    public async Task OwnerPausePreventsNewCapturesAndResumeReconcilesThem()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        IntakeHarness monitor = new(catalog, inbox,
            TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        Assert.True(monitor.SetPaused(true).Paused);
        using CancellationTokenSource cancellation = new();
        Task running = monitor.RunAsync(cancellation.Token);
        try
        {
            string first = Path.Combine(inbox, "paused.md");
            File.WriteAllText(first, "Written while paused");
            await Task.Delay(350);
            Assert.Empty(catalog.Library.List());
            Assert.Equal("paused", monitor.Snapshot().State);

            Assert.False(monitor.SetPaused(false).Paused);
            await WaitForAsync(() =>
                catalog.Library.List().FirstOrDefault()?.ProcessingState == "completed" &&
                monitor.Snapshot() is { State: "watching" or "degraded", ErrorCount: 0, LastSuccessfulScanUtc: not null } &&
                monitor.Snapshot().ImportedSinceStart == 1);
            Assert.Equal(1, monitor.Snapshot().ImportedSinceStart);
            Assert.Single(catalog.Search.Query("Written"));

            monitor.SetPaused(true);
            File.WriteAllText(first, "Completely different revision");
            File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(1));
            await Task.Delay(350);
            Assert.Equal(1, Assert.Single(catalog.Library.List()).Revision);

            monitor.SetPaused(false);
            // Capture and processing are independent; wait for both to settle.
            await WaitForAsync(() =>
                catalog.Library.List().FirstOrDefault() is { Revision: 2, ProcessingState: "completed" } &&
                monitor.Snapshot().ImportedSinceStart == 2);
            Assert.Empty(catalog.Search.Query("Written"));
            Assert.Single(catalog.Search.Query("Completely"));
            await catalog.Originals.VerifyAsync(Assert.Single(catalog.Library.List()).ItemId);
        }
        finally
        {
            await cancellation.CancelAsync();
            await running;
        }

        Assert.Equal("stopped", monitor.Snapshot().State);
    }

    [Fact]
    public async Task OfflineWatcherRequiresTrustedReapprovalWithoutRecreatingOriginals()
    {
        string removable = Path.Combine(workspace, "removable");
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        IntakeHarness monitor = new(catalog, removable,
            TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        using CancellationTokenSource cancellation = new();
        Task running = monitor.RunAsync(cancellation.Token);
        try
        {
            await WaitForAsync(() => monitor.Snapshot().State == "offline");
            Assert.Null(monitor.Snapshot().LastSuccessfulScanUtc);
            Assert.Contains("Reconnect", monitor.Snapshot().Problem);

            Directory.CreateDirectory(removable);
            File.WriteAllText(Path.Combine(removable, "reconnected.md"), "# Reconnected");
            await WaitForAsync(() => monitor.Snapshot().State == "reapproval_required");
            Assert.Empty(catalog.Library.List());
            monitor.Monitor.ApproveRoot(removable);
            await WaitForAsync(() =>
                catalog.Library.List().FirstOrDefault()?.ProcessingState == "completed" &&
                monitor.Snapshot() is { State: "watching" or "degraded", ErrorCount: 0, LastSuccessfulScanUtc: not null } &&
                monitor.Snapshot().ImportedSinceStart == 1);
            LibraryItem saved = Assert.Single(catalog.Library.List());
            Assert.NotNull(monitor.Snapshot().LastSuccessfulScanUtc);
            Assert.Single(catalog.Search.Query("Reconnected"));

            Directory.Delete(removable, recursive: true);
            await WaitForAsync(() => monitor.Snapshot().State == "offline");
            await catalog.Originals.VerifyAsync(saved.ItemId);
            Assert.Single(catalog.Library.List());
            Assert.Single(catalog.Search.Query("Reconnected"));
        }
        finally
        {
            await cancellation.CancelAsync();
            await running;
        }
    }

    [Fact]
    public async Task DesktopLibraryRemainsReadableWhenApprovedInboxIsOffline()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        using MemoryStream source = new(
            System.Text.Encoding.UTF8.GetBytes("Preserved offline knowledge"));
        CapturedOriginal captured = await catalog.Originals.CaptureFileAsync(
            Path.Combine(workspace, "unmounted.md"), source, 256, "offline-original");
        string disconnected = Path.Combine(workspace, "not-mounted");
        IntakeHarness monitor = new(catalog, disconnected,
            TimeSpan.Zero, TimeSpan.FromMilliseconds(100));
        using CancellationTokenSource cancellation = new();
        Task running = monitor.RunAsync(cancellation.Token);
        try
        {
            await WaitForAsync(() => monitor.Snapshot().State == "offline");
            DesktopProtocol service = TestDesktop.Protocol(catalog, monitor: monitor.Monitor, processing: monitor.Processing);
            using JsonDocument status = System.Text.Json.JsonDocument.Parse(
                await service.HandleAsync(
                    """{"requestId":"status","action":"intakeStatus"}"""));
            Assert.Equal("offline", status.RootElement.GetProperty("data")
                .GetProperty("status").GetProperty("state").GetString());

            using JsonDocument library = System.Text.Json.JsonDocument.Parse(
                await service.HandleAsync(
                    """{"requestId":"library","action":"library"}"""));
            JsonElement item = Assert.Single(library.RootElement.GetProperty("data")
                .GetProperty("items").EnumerateArray());
            Assert.Equal(captured.ItemId.ToString("D"),
                item.GetProperty("itemId").GetString());
            await catalog.Originals.VerifyAsync(captured.ItemId);
            Assert.False(Directory.Exists(disconnected));
        }
        finally
        {
            await cancellation.CancelAsync();
            await running;
        }
    }

    private static async Task WaitForAsync(Func<bool> predicate)
    {
        DateTime timeout = DateTime.UtcNow.AddSeconds(10);
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
