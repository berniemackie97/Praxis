using System.Text;
using Praxis.Infrastructure.Catalog;
using Praxis.Infrastructure.Intake;
using Praxis.Infrastructure.Originals;
using Praxis.Infrastructure.Processing;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Processing;

public sealed class LocalProcessingPipelineTests : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private readonly string workspace = Path.Combine(TestPaths.Temp,
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
            UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(workspace, mode);
            File.SetUnixFileMode(root, mode);
        }
    }

    [Fact]
    public async Task BackgroundPipelineExtractsAndProposesWithoutAnIntakeScan()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        LocalProcessingPipeline pipeline = LocalProcessingPipeline.CreateDefault(catalog, TimeSpan.FromMilliseconds(50));
        using CancellationTokenSource cancellation = new();
        Task running = pipeline.RunAsync(cancellation.Token);

        CapturedOriginal captured = await Capture(catalog, "plan.md", "# Architecture\nBody", "plan");
        pipeline.Wake();
        await WaitForAsync(() => catalog.Proposals.Outcome(captured.SourceId) == "generated");

        Assert.Equal("Architecture", Assert.Single(catalog.Proposals.List(captured.SourceId)).Value);
        Assert.Single(catalog.Search.Query("Architecture"));
        await cancellation.CancelAsync();
        await running;
    }

    [Fact]
    public async Task SlowProcessingDoesNotBlockWatchedIntakeCapture()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        BlockingHandler blocking = new(LocalProcessors.SourceText, maxConcurrency: 1);
        LocalProcessingPipeline pipeline = new(catalog, [blocking], TimeSpan.FromMilliseconds(50));
        LocalWatchedIntake intake = new(catalog, inbox, TimeSpan.Zero, onCaptured: pipeline.Wake);
        using CancellationTokenSource cancellation = new();
        Task running = pipeline.RunAsync(cancellation.Token);

        File.WriteAllText(Path.Combine(inbox, "first.txt"), "first");
        DateTimeOffset time = DateTimeOffset.UtcNow;
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
        using LocalCatalog catalog = LocalCatalog.Open(root);
        for (int index = 0; index < 5; index++)
        {
            await Capture(catalog, $"item{index}.txt", $"text {index}", $"key{index}");
        }

        BlockingHandler blocking = new(LocalProcessors.SourceText, maxConcurrency: 2);
        LocalProcessingPipeline pipeline = new(catalog, [blocking], TimeSpan.FromMilliseconds(50));
        using CancellationTokenSource cancellation = new();
        Task running = pipeline.RunAsync(cancellation.Token);

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
        using LocalCatalog catalog = LocalCatalog.Open(root);
        CapturedOriginal broken = await Capture(catalog, "broken.txt", "boom", "broken");
        CapturedOriginal healthy = await Capture(catalog, "healthy.txt", "fine", "healthy");
        List<string> reports = new();
        FaultingHandler faulting = new(catalog, broken.ItemId);
        LocalProcessingPipeline pipeline = new(catalog, [faulting],
            TimeSpan.FromMilliseconds(50), status: message => { lock (reports) { reports.Add(message); } });

        Assert.True(await pipeline.ProcessNextAsync());
        Assert.True(await pipeline.ProcessNextAsync());
        Assert.False(await pipeline.ProcessNextAsync());

        ProcessingJob[] jobs = catalog.Processing.List()
            .Where(job => job.ProcessorKey == LocalProcessors.SourceText).ToArray();
        ProcessingJob failed = Assert.Single(jobs, job => job.ItemId == broken.ItemId);
        Assert.Equal("failed", failed.State);
        Assert.Equal(LocalProcessingPipeline.FaultCode, failed.FailureCode);
        Assert.Equal("completed", Assert.Single(jobs, job => job.ItemId == healthy.ItemId).State);
        string report = Assert.Single(reports);
        Assert.Contains(nameof(InvalidOperationException), report, StringComparison.Ordinal);
        Assert.DoesNotContain("secret detail", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownRuntimeFailureCodeCannotLeakThroughStatus()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        await Capture(catalog, "secret.txt", "sensitive content", "private");
        List<string> reports = new();
        LocalProcessingPipeline pipeline = new(catalog, [new UntrustedRuntimeCodeHandler()], status: reports.Add);

        Assert.True(await pipeline.ProcessNextAsync());

        string report = Assert.Single(reports);
        Assert.Contains("runtime_unavailable", report, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-bearing-code", report, StringComparison.Ordinal);
        ProcessingJob job = Assert.Single(catalog.Processing.List());
        Assert.Equal("queued", job.State);
        Assert.Equal(0, job.Attempt);
    }

    [Fact]
    public async Task ShutdownLeavesInterruptedLeaseRecoverableInsteadOfFailed()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        await Capture(catalog, "slow.txt", "slow", "slow");
        BlockingHandler blocking = new(LocalProcessors.SourceText, maxConcurrency: 1);
        LocalProcessingPipeline pipeline = new(catalog, [blocking], TimeSpan.FromMilliseconds(50));
        using CancellationTokenSource cancellation = new();
        Task running = pipeline.RunAsync(cancellation.Token);
        await blocking.Started.Task.WaitAsync(Deadline);

        await cancellation.CancelAsync();
        await running.WaitAsync(Deadline);

        ProcessingJob job = Assert.Single(catalog.Processing.List());
        Assert.Equal("running", job.State);
        Assert.Null(job.FailureCode);
        Assert.NotNull(catalog.Processing.TryClaim(TimeSpan.FromSeconds(1),
            nowUtc: job.LeaseUntil!.Value.AddSeconds(1), processorKey: LocalProcessors.SourceText));
    }

    [Fact]
    public void HandlersMustHaveDistinctProcessorsAndBoundedConcurrency()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        Assert.Throws<ArgumentException>(() => new LocalProcessingPipeline(catalog, []));
        Assert.Throws<ArgumentException>(() => new LocalProcessingPipeline(catalog,
            [new BlockingHandler("same", 1), new BlockingHandler("same", 1)]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LocalProcessingPipeline(catalog,
            [new BlockingHandler("bounded", 17)]));
    }

    private async Task<CapturedOriginal> Capture(
        LocalCatalog catalog, string filename, string contents, string key)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(contents));
        return await catalog.Originals.CaptureFileAsync(
            Path.Combine(workspace, filename), stream, 20_000, key);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow + Deadline;
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

    private sealed class UntrustedRuntimeCodeHandler : ILocalProcessingHandler
    {
        public string ProcessorKey => LocalProcessors.SourceText;
        public TimeSpan LeaseDuration => TimeSpan.FromMinutes(1);
        public int MaxConcurrency => 1;

        public bool TryReopenWork() => false;

        public Task ProcessAsync(ProcessingLease lease, CancellationToken cancellationToken) =>
            throw new ProcessorUnavailableException("secret-bearing-code");
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
            int now = Interlocked.Increment(ref active);
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
