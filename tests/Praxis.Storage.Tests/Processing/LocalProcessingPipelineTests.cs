using System.Text;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class LocalProcessingPipelineTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private readonly string workspace = Path.Combine(Path.GetTempPath(),
        "praxis-pipeline-" + Guid.NewGuid().ToString("N"));
    private readonly string root;
    private readonly string inbox;

    public LocalProcessingPipelineTests()
    {
        root = Path.Combine(workspace, "catalog");
        inbox = Path.Combine(workspace, "inbox");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(inbox);
        if (!OperatingSystem.IsWindows())
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(workspace, mode);
            File.SetUnixFileMode(root, mode);
        }
    }

    [Fact]
    public async Task BackgroundPipelineExtractsAndProposesWithoutAnIntakeScan()
    {
        using var catalog = LocalCatalog.Open(root);
        var pipeline = LocalProcessingPipeline.CreateDefault(catalog, TimeSpan.FromMilliseconds(50));
        using var cancellation = new CancellationTokenSource();
        var running = pipeline.RunAsync(cancellation.Token);

        var captured = await Capture(catalog, "plan.md", "# Architecture\nBody", "plan");
        pipeline.Wake();
        await WaitForAsync(() => catalog.Topics.Outcome(captured.SourceId) == "generated");

        Assert.Equal("Architecture", Assert.Single(catalog.Topics.List(captured.SourceId)).Value);
        Assert.Single(catalog.Search.Query("Architecture"));
        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task SlowProcessingDoesNotBlockWatchedIntakeCapture()
    {
        using var catalog = LocalCatalog.Open(root);
        var blocking = new BlockingHandler(LocalProcessors.SourceText, maxConcurrency: 1);
        var pipeline = new LocalProcessingPipeline(catalog, [blocking], TimeSpan.FromMilliseconds(50));
        var intake = new LocalWatchedIntake(catalog, inbox, TimeSpan.Zero, onCaptured: pipeline.Wake);
        using var cancellation = new CancellationTokenSource();
        var running = pipeline.RunAsync(cancellation.Token);

        File.WriteAllText(Path.Combine(inbox, "first.txt"), "first");
        var time = DateTimeOffset.UtcNow;
        await intake.ReconcileAsync(time);
        Assert.Equal(1, await intake.ReconcileAsync(time.AddSeconds(1)));
        await blocking.Started.Task.WaitAsync(Deadline);

        // The only text lease is held by a run that will not finish; capture still proceeds.
        File.WriteAllText(Path.Combine(inbox, "second.txt"), "second");
        await intake.ReconcileAsync(time.AddSeconds(2));
        Assert.Equal(1, await intake.ReconcileAsync(time.AddSeconds(3)));
        Assert.Equal(2, catalog.Library.List().Count);

        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task ConcurrencyIsBoundedPerHandler()
    {
        using var catalog = LocalCatalog.Open(root);
        for (var index = 0; index < 5; index++)
        {
            await Capture(catalog, $"item{index}.txt", $"text {index}", $"key{index}");
        }

        var blocking = new BlockingHandler(LocalProcessors.SourceText, maxConcurrency: 2);
        var pipeline = new LocalProcessingPipeline(catalog, [blocking], TimeSpan.FromMilliseconds(50));
        using var cancellation = new CancellationTokenSource();
        var running = pipeline.RunAsync(cancellation.Token);

        await WaitForAsync(() => blocking.Active == 2);
        await Task.Delay(200);
        Assert.Equal(2, blocking.Active);
        Assert.Equal(2, blocking.MaximumActive);
        Assert.Equal(2, catalog.Processing.List().Count(job => job.State == "running"));
        Assert.Equal(3, catalog.Processing.List().Count(job => job.State == "queued"));

        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task HandlerFaultFailsOnlyItsJobAndProcessingContinues()
    {
        using var catalog = LocalCatalog.Open(root);
        var broken = await Capture(catalog, "broken.txt", "boom", "broken");
        var healthy = await Capture(catalog, "healthy.txt", "fine", "healthy");
        var reports = new List<string>();
        var faulting = new FaultingHandler(catalog, broken.ItemId);
        var pipeline = new LocalProcessingPipeline(catalog, [faulting],
            TimeSpan.FromMilliseconds(50), status: message => { lock (reports) { reports.Add(message); } });

        Assert.True(await pipeline.ProcessNextAsync());
        Assert.True(await pipeline.ProcessNextAsync());
        Assert.False(await pipeline.ProcessNextAsync());

        var jobs = catalog.Processing.List()
            .Where(job => job.ProcessorKey == LocalProcessors.SourceText).ToArray();
        var failed = Assert.Single(jobs, job => job.ItemId == broken.ItemId);
        Assert.Equal("failed", failed.State);
        Assert.Equal(LocalProcessingPipeline.FaultCode, failed.FailureCode);
        Assert.Equal("completed", Assert.Single(jobs, job => job.ItemId == healthy.ItemId).State);
        var report = Assert.Single(reports);
        Assert.Contains(nameof(InvalidOperationException), report, StringComparison.Ordinal);
        Assert.DoesNotContain("secret detail", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShutdownLeavesInterruptedLeaseRecoverableInsteadOfFailed()
    {
        using var catalog = LocalCatalog.Open(root);
        await Capture(catalog, "slow.txt", "slow", "slow");
        var blocking = new BlockingHandler(LocalProcessors.SourceText, maxConcurrency: 1);
        var pipeline = new LocalProcessingPipeline(catalog, [blocking], TimeSpan.FromMilliseconds(50));
        using var cancellation = new CancellationTokenSource();
        var running = pipeline.RunAsync(cancellation.Token);
        await blocking.Started.Task.WaitAsync(Deadline);

        await cancellation.CancelAsync();
        await running.WaitAsync(Deadline);

        var job = Assert.Single(catalog.Processing.List());
        Assert.Equal("running", job.State);
        Assert.Null(job.FailureCode);
        Assert.NotNull(catalog.Processing.TryClaim(TimeSpan.FromSeconds(1),
            nowUtc: job.LeaseUntil!.Value.AddSeconds(1), processorKey: LocalProcessors.SourceText));
    }

    [Fact]
    public void HandlersMustHaveDistinctProcessorsAndBoundedConcurrency()
    {
        using var catalog = LocalCatalog.Open(root);
        Assert.Throws<ArgumentException>(() => new LocalProcessingPipeline(catalog, []));
        Assert.Throws<ArgumentException>(() => new LocalProcessingPipeline(catalog,
            [new BlockingHandler("same", 1), new BlockingHandler("same", 1)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalProcessingPipeline(catalog,
            [new BlockingHandler("bounded", 17)]));
    }

    private async Task<CapturedOriginal> Capture(
        LocalCatalog catalog, string filename, string contents, string key)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(contents));
        return await catalog.Originals.CaptureFileAsync(
            Path.Combine(workspace, filename), stream, 20_000, key);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The pipeline did not converge.");
            await Task.Delay(20);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private sealed class BlockingHandler(string processorKey, int maxConcurrency) : ILocalProcessingHandler
    {
        private int active;
        private int maximumActive;

        public string ProcessorKey => processorKey;
        public TimeSpan LeaseDuration => TimeSpan.FromMinutes(1);
        public int MaxConcurrency => maxConcurrency;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Active => Volatile.Read(ref active);
        public int MaximumActive => Volatile.Read(ref maximumActive);

        public bool TryReopenWork() => false;

        public async Task ProcessAsync(ProcessingLease lease, CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref active);
            int observed;
            while (now > (observed = Volatile.Read(ref maximumActive)) &&
                Interlocked.CompareExchange(ref maximumActive, now, observed) != observed)
            {
            }

            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        }
    }

    private sealed class FaultingHandler(LocalCatalog catalog, Guid faultingItem) : ILocalProcessingHandler
    {
        public string ProcessorKey => LocalProcessors.SourceText;
        public TimeSpan LeaseDuration => TimeSpan.FromMinutes(1);
        public int MaxConcurrency => 1;

        public bool TryReopenWork() => false;

        public Task ProcessAsync(ProcessingLease lease, CancellationToken cancellationToken)
        {
            if (lease.ItemId == faultingItem)
            {
                throw new InvalidOperationException("secret detail");
            }

            catalog.Processing.Finish(lease, ProcessingCompletion.Completed);
            return Task.CompletedTask;
        }
    }
}
