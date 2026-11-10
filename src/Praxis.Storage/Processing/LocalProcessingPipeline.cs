using Praxis.Storage.Catalog;
using Praxis.Storage.Enrichment;
using Praxis.Storage.Enrichment.Semantic;

namespace Praxis.Storage.Processing;

/// <summary>
/// Runs leased processing jobs for registered handlers, independently of source intake.
/// Concurrency is bounded per handler, idle waits are woken by <see cref="Wake"/> or a
/// periodic poll (which also recovers expired leases), and an unexpected handler fault is
/// recorded on the job instead of stopping the pipeline.
/// </summary>
public sealed class LocalProcessingPipeline
{
    internal const string FaultCode = "processor_fault";
    private const int MaximumAttempts = 3;

    private readonly LocalCatalog catalog;
    private readonly IReadOnlyList<ILocalProcessingHandler> handlers;
    private readonly TimeSpan idlePoll;
    private readonly Action<string> report;
    private readonly SemaphoreSlim wakeSignal = new(0, 1);
    private static readonly TimeSpan MinimumBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(10);
    private readonly HashSet<string> reopenExhausted = new(StringComparer.Ordinal);
    private readonly Lock backoffGate = new();
    private readonly Dictionary<string, (DateTimeOffset Until, TimeSpan Next)> backoff =
        new(StringComparer.Ordinal);
    private int wakePending;
    private int nextHandler;

    public LocalProcessingPipeline(LocalCatalog catalog, IReadOnlyList<ILocalProcessingHandler> handlers, TimeSpan? idlePoll = null, Action<string>? status = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(handlers);
        if (handlers.Count == 0 || handlers.Select(handler => handler.ProcessorKey).Distinct(StringComparer.Ordinal).Count() != handlers.Count)
        {
            throw new ArgumentException("Register at least one handler, each for a distinct processor.", nameof(handlers));
        }

        foreach (ILocalProcessingHandler handler in handlers)
        {
            ProcessingJobKey.ValidateProcessor(handler.ProcessorKey);
            if (handler.MaxConcurrency is < 1 or > 16)
            {
                throw new ArgumentOutOfRangeException(nameof(handlers), "Handler concurrency must be 1 to 16.");
            }
        }

        this.idlePoll = idlePoll ?? TimeSpan.FromSeconds(15);
        if (this.idlePoll < TimeSpan.FromMilliseconds(50))
        {
            throw new ArgumentOutOfRangeException(nameof(idlePoll));
        }

        this.catalog = catalog;
        this.handlers = handlers;
        report = status ?? (_ => { });
    }

    /// <summary>
    /// The local processors: extraction, heading proposals, segmentation and model enrichment
    /// with the owner's configured local model (deferred while none is enabled).
    /// </summary>
    public static LocalProcessingPipeline CreateDefault(
        LocalCatalog catalog, TimeSpan? idlePoll = null, Action<string>? status = null) =>
        new(catalog,
            [
                new LocalTextProcessor(catalog), new LocalHeadingProposer(catalog),
                new LocalTextSegmenter(catalog), new LocalSemanticExtractor(catalog, ModelConnection.Open)
            ],
            idlePoll, status);

    /// <summary>
    /// Signals that new work may be available and clears runtime backoffs, so an owner action
    /// such as enabling a model is retried immediately. Coalesces repeated signals.
    /// </summary>
    public void Wake()
    {
        lock (backoffGate)
        {
            backoff.Clear();
        }

        if (Interlocked.CompareExchange(ref wakePending, 1, 0) == 0)
        {
            wakeSignal.Release();
        }
    }

    /// <summary>Claims and completes a single job, trying handlers in rotation.</summary>
    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (ILocalProcessingHandler Handler, ProcessingLease Lease)? claimed = TryClaimNext(IsAvailable) ?? (ReopenWork() ? TryClaimNext(IsAvailable) : null);
        if (claimed is not { } job)
        {
            return false;
        }

        await ExecuteAsync(job.Handler, job.Lease, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return true;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Dictionary<ILocalProcessingHandler, int> active = handlers.ToDictionary(handler => handler, _ => 0);
        // Not keyed by Task: runs that finish synchronously share the runtime's cached task.
        List<(Task Run, ILocalProcessingHandler Handler)> running = new();
        Task? wake = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                while (TryClaimNext(handler =>
                    active[handler] < handler.MaxConcurrency && IsAvailable(handler)) is { } job)
                {
                    active[job.Handler]++;
                    running.Add((ExecuteAsync(job.Handler, job.Lease, cancellationToken), job.Handler));
                }

                if (running.Count == 0 && ReopenWork())
                {
                    continue;
                }

                // One outstanding waiter at a time, so a signal is never consumed by an
                // abandoned wait while a run completes first.
                wake ??= WaitForWakeAsync(cancellationToken);
                await Task.WhenAny(running.Select(entry => entry.Run).Append(wake));
                if (wake.IsCompleted)
                {
                    Task signaled = wake;
                    wake = null;
                    await signaled;
                }

                foreach ((Task Run, ILocalProcessingHandler Handler) finished in running.Where(entry => entry.Run.IsCompleted).ToArray())
                {
                    running.Remove(finished);
                    active[finished.Handler]--;
                    await finished.Run;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Runs observe the same token; an interrupted lease expires and is reclaimed later.
            await Task.WhenAll(running.Select(entry => entry.Run));
        }
    }

    private (ILocalProcessingHandler Handler, ProcessingLease Lease)? TryClaimNext(Func<ILocalProcessingHandler, bool> hasCapacity)
    {
        for (int tried = 0; tried < handlers.Count; tried++)
        {
            ILocalProcessingHandler handler = handlers[nextHandler];
            nextHandler = (nextHandler + 1) % handlers.Count;
            if (!hasCapacity(handler))
            {
                continue;
            }

            ProcessingLease? lease = catalog.Processing.TryClaim(handler.LeaseDuration, MaximumAttempts, processorKey: handler.ProcessorKey);
            if (lease is not null)
            {
                return (handler, lease);
            }
        }

        return null;
    }

    private bool ReopenWork()
    {
        bool reopened = false;
        foreach (ILocalProcessingHandler handler in handlers)
        {
            if (reopenExhausted.Contains(handler.ProcessorKey))
            {
                continue;
            }

            if (handler.TryReopenWork())
            {
                reopened = true;
            }
            else
            {
                reopenExhausted.Add(handler.ProcessorKey);
            }
        }

        return reopened;
    }

    private async Task ExecuteAsync(ILocalProcessingHandler handler, ProcessingLease lease, CancellationToken cancellationToken)
    {
        try
        {
            await handler.ProcessAsync(lease, cancellationToken);
            ClearBackoff(handler);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Interrupted by shutdown: the lease expires and the job is reclaimed later.
        }
        catch (ProcessorUnavailableException unavailable)
        {
            catalog.Processing.Defer(lease);
            var delay = BackOff(handler);
            report($"Processor {handler.ProcessorKey} is waiting for its runtime " +
                $"({unavailable.Code}); retrying in {delay.TotalSeconds:0} s or on the next wake.");
        }
        catch (Exception exception)
        {
            // Isolation boundary: a defective handler fails its own job, durably and without
            // persisting exception text, rather than stopping every other processor.
            catalog.Processing.Finish(lease, ProcessingCompletion.Failed, FaultCode);
            report($"Processor {handler.ProcessorKey} faulted on item {lease.ItemId:D} revision {lease.Revision}: {exception.GetType().Name}");
        }
    }

    private bool IsAvailable(ILocalProcessingHandler handler)
    {
        lock (backoffGate)
        {
            return !backoff.TryGetValue(handler.ProcessorKey, out var state) ||
                state.Until <= DateTimeOffset.UtcNow;
        }
    }

    private TimeSpan BackOff(ILocalProcessingHandler handler)
    {
        lock (backoffGate)
        {
            var delay = backoff.TryGetValue(handler.ProcessorKey, out var state) ? state.Next : MinimumBackoff;
            var next = delay * 2 > MaximumBackoff ? MaximumBackoff : delay * 2;
            backoff[handler.ProcessorKey] = (DateTimeOffset.UtcNow + delay, next);
            return delay;
        }
    }

    private void ClearBackoff(ILocalProcessingHandler handler)
    {
        lock (backoffGate)
        {
            backoff.Remove(handler.ProcessorKey);
        }
    }

    private async Task WaitForWakeAsync(CancellationToken cancellationToken)
    {
        await wakeSignal.WaitAsync(idlePoll, cancellationToken);
        Interlocked.Exchange(ref wakePending, 0);
    }
}
