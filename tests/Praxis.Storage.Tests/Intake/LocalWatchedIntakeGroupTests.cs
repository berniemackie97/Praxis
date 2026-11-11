using System.Text.Json;
using Praxis.Storage.Catalog;
using Praxis.Storage.Desktop;
using Praxis.Storage.Intake;
using Praxis.Storage.Library;
using Praxis.Storage.Processing;
using Praxis.Storage.Tests.Support;
using Xunit;

namespace Praxis.Storage.Tests.Intake;

public sealed class LocalWatchedIntakeGroupTests : IDisposable
{
    private readonly string workspace = Path.Combine(TestPaths.Temp, "praxis-multi-root-" + Guid.NewGuid().ToString("N"));
    private readonly string dataRoot;
    private readonly string first;
    private readonly string second;

    public LocalWatchedIntakeGroupTests()
    {
        dataRoot = Path.Combine(workspace, "catalog");
        first = Path.Combine(workspace, "primary");
        second = Path.Combine(workspace, "secondary");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dataRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task IndependentRootsUseOneCatalogAndPreserveRevisionAndOriginalIdentity()
    {
        string a = Path.Combine(first, "primary.md");
        string b = Path.Combine(second, "secondary.txt");
        File.WriteAllText(a, "violet primary content");
        File.WriteAllText(b, "amber secondary content");
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        LocalProcessingPipeline processing = LocalProcessingPipeline.CreateDefault(catalog);
        LocalWatchedIntakeGroup group = new(catalog, [first, second], onCaptured: processing.Wake,
            quietPeriod: TimeSpan.Zero);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Equal(0, await group.ReconcileAsync(now));
        Assert.Equal(2, await group.ReconcileAsync(now.AddSeconds(1)));
        while (await processing.ProcessNextAsync())
        {
        }

        LocalLibraryItem[] items = catalog.Library.List().ToArray();
        Assert.Equal(2, items.Length);
        Assert.NotEqual(items[0].SourceId, items[1].SourceId);
        Assert.Single(catalog.Search.Query("violet"));
        Assert.Single(catalog.Search.Query("amber"));
        Assert.Equal("watching", group.Snapshot().State);
        Assert.Equal(2, group.Snapshot().ImportedSinceStart);
        Assert.Equal(2, group.RootStatuses().Count);
        foreach (LocalLibraryItem item in items)
        {
            await catalog.Originals.VerifyAsync(item.ItemId);
        }

        Guid original = Assert.Single(items, item => item.SourceLocator == b).ItemId;
        File.WriteAllText(b, "cobalt secondary revision");
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddSeconds(10));
        Assert.Equal(0, await group.ReconcileAsync(now.AddSeconds(2)));
        Assert.Equal(1, await group.ReconcileAsync(now.AddSeconds(3)));
        while (await processing.ProcessNextAsync())
        {
        }
        LocalLibraryItem revised = Assert.Single(catalog.Library.List(), item => item.SourceLocator == b);
        Assert.Equal(original, revised.ItemId);
        Assert.Equal(2, revised.Revision);
        Assert.Empty(catalog.Search.Query("amber"));
        Assert.Single(catalog.Search.Query("cobalt"));
    }

    [Fact]
    public void RejectsDuplicateNestedCatalogAndExcessiveRootsBeforeStartingWatchers()
    {
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        Assert.Throws<ArgumentException>(() => new LocalWatchedIntakeGroup(catalog, [first, first]));
        string child = Path.Combine(first, "nested");
        Directory.CreateDirectory(child);
        Assert.Throws<ArgumentException>(() => new LocalWatchedIntakeGroup(catalog, [first, child]));
        Assert.Throws<ArgumentException>(() => new LocalWatchedIntakeGroup(catalog, [workspace]));
        Assert.Throws<ArgumentException>(() => new LocalWatchedIntakeGroup(catalog, [dataRoot]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalWatchedIntakeGroup(catalog, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalWatchedIntakeGroup(catalog,
            [first, second, Path.Combine(workspace, "three"), Path.Combine(workspace, "four"),
                Path.Combine(workspace, "five")]));
        Assert.Throws<ArgumentException>(() => new LocalWatchedIntakeGroup(catalog, ["relative-inbox"]));
    }

    [Fact]
    public async Task PauseAndResumeApplyToEveryApprovedRootWithoutDuplicates()
    {
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        LocalWatchedIntakeGroup group = new(catalog, [first, second], quietPeriod: TimeSpan.Zero);
        group.SetPaused(true);
        File.WriteAllText(Path.Combine(first, "a.txt"), "violet paused");
        File.WriteAllText(Path.Combine(second, "b.txt"), "amber paused");
        Assert.Equal(0, await group.ReconcileAsync(DateTimeOffset.UtcNow));
        Assert.Empty(catalog.Library.List());
        Assert.All(group.RootStatuses(), root => Assert.True(root.Status.Paused));
        group.SetPaused(false);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await group.ReconcileAsync(now);
        Assert.Equal(2, await group.ReconcileAsync(now.AddSeconds(1)));
        Assert.Equal(0, await group.ReconcileAsync(now.AddSeconds(2)));
        Assert.Equal(2, catalog.Library.List().Count);
    }

    [Fact]
    public async Task OfflineSecondaryDoesNotBlockPrimaryAndCannotReattachWithoutApproval()
    {
        string disconnected = Path.Combine(workspace, "not-present");
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        LocalProcessingPipeline processing = LocalProcessingPipeline.CreateDefault(catalog);
        LocalWatchedIntakeGroup group = new(catalog, [first, disconnected], onCaptured: processing.Wake,
            quietPeriod: TimeSpan.Zero, rescanInterval: TimeSpan.FromMilliseconds(100));
        using CancellationTokenSource cancellation = new();
        Task running = group.RunAsync(cancellation.Token);
        try
        {
            await WaitForAsync(() => group.RootStatuses()[1].Status.State == "offline");
            File.WriteAllText(Path.Combine(first, "a.md"), "violet online root");
            await WaitForAsync(() => catalog.Library.List().Count == 1);
            Assert.Equal("degraded", group.Snapshot().State);
            Directory.CreateDirectory(disconnected);
            File.WriteAllText(Path.Combine(disconnected, "b.txt"), "amber offline return");
            await WaitForAsync(() => group.RootStatuses()[1].Status.State == "reapproval_required");
            Assert.Single(catalog.Library.List());
            Assert.Throws<ArgumentException>(() => group.ApproveRoot(Path.Combine(workspace, "unapproved")));
            group.ApproveRoot(disconnected);
            await WaitForAsync(() => catalog.Library.List().Count == 2);
            foreach (LocalLibraryItem item in catalog.Library.List())
            {
                await catalog.Originals.VerifyAsync(item.ItemId);
            }
        }
        finally
        {
            await cancellation.CancelAsync();
            await running;
        }
    }

    [Fact]
    public async Task ActualMacNotificationsCaptureDropsInBothRootsWithoutPolling()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        LocalWatchedIntakeGroup group = new(catalog, [first, second],
            quietPeriod: TimeSpan.FromMilliseconds(300), rescanInterval: TimeSpan.FromMinutes(5));
        using CancellationTokenSource cancellation = new();
        Task running = group.RunAsync(cancellation.Token);
        try
        {
            await WaitForAsync(() => group.RootStatuses().All(root => root.Status.State == "watching"));
            File.WriteAllText(Path.Combine(first, "event-a.md"), "violet event");
            File.WriteAllText(Path.Combine(second, "event-b.txt"), "amber event");
            await WaitForAsync(() => catalog.Library.List().Count == 2);
            Assert.All(group.RootStatuses(), root => Assert.Equal(1, root.Status.ImportedSinceStart));
            foreach (LocalLibraryItem item in catalog.Library.List())
            {
                await catalog.Originals.VerifyAsync(item.ItemId);
            }
        }
        finally
        {
            await cancellation.CancelAsync();
            await running;
        }
    }

    [Fact]
    public async Task PrivateProtocolReportsIndividualRootsAndRestrictsApprovalAndQuickCapture()
    {
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        LocalWatchedIntakeGroup group = new(catalog, [first, second], quietPeriod: TimeSpan.Zero);
        LocalDesktopProtocol protocol = new(catalog, group: group);
        using JsonDocument initial = await Send(protocol, """{"requestId":"status","action":"intakeStatus"}""");
        JsonElement roots = initial.RootElement.GetProperty("data").GetProperty("roots");
        Assert.Equal(2, roots.GetArrayLength());
        Assert.Equal(first, roots[0].GetProperty("path").GetString());
        Assert.Equal(second, roots[1].GetProperty("path").GetString());

        using JsonDocument invalid = await Send(protocol,
            $$"""{"requestId":"denied","action":"intakeApproveRoot","path":"{{dataRoot}}"}""");
        Assert.False(invalid.RootElement.GetProperty("ok").GetBoolean());

        using JsonDocument approved = await Send(protocol,
            $$"""{"requestId":"yes","action":"intakeApproveRoot","path":"{{second}}"}""");
        Assert.True(approved.RootElement.GetProperty("ok").GetBoolean());

        string missing = Path.Combine(workspace, "not-trusted");
        LocalWatchedIntakeGroup absent = new(catalog, [missing]);
        LocalDesktopProtocol guarded = new(catalog, new LocalQuickCapture(catalog, missing), group: absent);
        using JsonDocument deniedNote = await Send(guarded,
            $$"""{"requestId":"note","action":"noteCapture","captureId":"{{Guid.NewGuid():D}}","text":"violet no write"}""");
        Assert.False(deniedNote.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(Directory.Exists(missing));
    }

    private static async Task<JsonDocument> Send(LocalDesktopProtocol protocol, string request) =>
        JsonDocument.Parse(await protocol.HandleAsync(request));

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
        }

        Assert.True(condition(), "Supervised roots did not reach the expected state.");
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
