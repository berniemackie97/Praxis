using Praxis.Domain.Processing;

namespace Praxis.Application.Processing;

/// <summary>
/// Runs leased processing jobs for registered handlers, independently of source intake.
/// Concurrency is bounded per handler, idle waits are woken by <see cref="Wake"/> or a
/// periodic poll (which also recovers expired leases), and an unexpected handler fault is
/// recorded on the job instead of stopping the pipeline.
/// </summary>
public sealed class ProcessingPipeline
{
    public const string FaultCode = "processor_fault";
    private const int MaximumAttempts = 3;

    private readonly IProcessingQueue queue;
    private readonly IProcessingControls? controls;
    public IReadOnlyList<string> Processors => handlers.Select(handler => handler.ProcessorKey).ToArray();
    private readonly TimeProvider clock;
    private readonly IReadOnlyList<IProcessingHandler> handlers;
    private readonly TimeSpan idlePoll;
    private readonly Action<string> report;
    private readonly SemaphoreSlim wakeSignal = new(0, 1);
    private static readonly TimeSpan MinimumBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaximumBackoff = TimeSpan.FromMinutes(10);
    /// <summary>
    /// How often a stage that had nothing to reopen is asked again, even while other work runs:
    /// reopening depends on state that changes at runtime (a model connected, a hold expired).
    /// </summary>
    private static readonly TimeSpan ReopenInterval = TimeSpan.FromMinutes(1);
    private readonly Dictionary<string, DateTimeOffset> reopenAfter = new(StringComparer.Ordinal);
    private readonly Lock backoffGate = new();
    private readonly Dictionary<string, (DateTimeOffset Until, TimeSpan Next)> backoff =
        new(StringComparer.Ordinal);
    private int wakePending;
    private int nextHandler;

    public ProcessingPipeline(IProcessingQueue queue, IReadOnlyList<IProcessingHandler> handlers, TimeProvider clock, TimeSpan? idlePoll = null, Action<string>? status = null, IProcessingControls? controls = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(handlers);
        if (handlers.Count == 0 || handlers.Select(handler => handler.ProcessorKey).Distinct(StringComparer.Ordinal).Count() != handlers.Count)
        {
            throw new ArgumentException("Register at least one handler, each for a distinct processor.", nameof(handlers));
        }

        foreach (IProcessingHandler handler in handlers)
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

        this.queue = queue;
        this.controls = controls;
        this.clock = clock;
        this.handlers = handlers;
        report = status ?? (_ => { });
    }

    /// <summary>
    /// Signals that new work may be available and clears runtime backoffs, so an owner action
    /// such as enabling a model is retried immediately. Coalesces repeated signals.
    /// </summary>
    public void Wake()
    {
        lock (backoffGate)
        {
            backoff.Clear();
            reopenAfter.Clear();
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
        (IProcessingHandler Handler, ProcessingLease Lease)? claimed = TryClaimNext(IsAvailable) ?? (ReopenWork() ? TryClaimNext(IsAvailable) : null);
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
        Dictionary<IProcessingHandler, int> active = handlers.ToDictionary(handler => handler, _ => 0);
        // Not keyed by Task: runs that finish synchronously share the runtime's cached task.
        List<(Task Run, IProcessingHandler Handler)> running = new();
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

                // Checked while other work runs too: a busy optional stage must not starve reopening.
                if (ReopenWork())
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

                foreach ((Task Run, IProcessingHandler Handler) finished in running.Where(entry => entry.Run.IsCompleted).ToArray())
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

    private (IProcessingHandler Handler, ProcessingLease Lease)? TryClaimNext(Func<IProcessingHandler, bool> hasCapacity)
    {
        for (int tried = 0; tried < handlers.Count; tried++)
        {
            IProcessingHandler handler = handlers[nextHandler];
            nextHandler = (nextHandler + 1) % handlers.Count;
            if (!hasCapacity(handler))
            {
                continue;
            }

            ProcessingLease? lease = queue.TryClaim(handler.LeaseDuration, MaximumAttempts, processorKey: handler.ProcessorKey);
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
        DateTimeOffset now = clock.GetUtcNow();
        foreach (IProcessingHandler handler in handlers)
        {
            if (controls?.IsPaused(handler.ProcessorKey) == true)
            {
                continue;
            }
            lock (backoffGate)
            {
                if (reopenAfter.TryGetValue(handler.ProcessorKey, out DateTimeOffset after) && now < after)
                {
                    continue;
                }
            }

            if (handler.TryReopenWork())
            {
                reopened = true;
            }
            else
            {
                lock (backoffGate)
                {
                    reopenAfter[handler.ProcessorKey] = now + ReopenInterval;
                }
            }
        }

        return reopened;
    }

    private async Task ExecuteAsync(IProcessingHandler handler, ProcessingLease lease, CancellationToken cancellationToken)
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
            queue.Defer(lease);
            TimeSpan delay = BackOff(handler);
            string cause = unavailable.Code switch
            {
                "model_disabled" or "model_unavailable" or "model_missing" or "model_server_error" => unavailable.Code,
                _ => "runtime_unavailable"
            };
            report($"Processing runtime unavailable ({cause}); retrying in {delay.TotalSeconds:0} s or on the next wake.");
        }
        catch (Exception exception)
        {
            // Isolation boundary: a defective handler fails its own job, durably and without
            // persisting exception text, rather than stopping every other processor.
            queue.Finish(lease, ProcessingCompletion.Failed, FaultCode);
            report($"Processing failed ({exception.GetType().Name}); inspect owner-local processing status.");
        }
    }

    private bool IsAvailable(IProcessingHandler handler)
    {
        if (controls?.IsPaused(handler.ProcessorKey) == true)
        {
            return false;
        }
        lock (backoffGate)
        {
            return !backoff.TryGetValue(handler.ProcessorKey, out (DateTimeOffset Until, TimeSpan Next) state) ||
                state.Until <= clock.GetUtcNow();
        }
    }

    private TimeSpan BackOff(IProcessingHandler handler)
    {
        lock (backoffGate)
        {
            TimeSpan delay = backoff.TryGetValue(handler.ProcessorKey, out (DateTimeOffset Until, TimeSpan Next) state) ? state.Next : MinimumBackoff;
            TimeSpan next = delay * 2 > MaximumBackoff ? MaximumBackoff : delay * 2;
            backoff[handler.ProcessorKey] = (clock.GetUtcNow() + delay, next);
            return delay;
        }
    }

    private void ClearBackoff(IProcessingHandler handler)
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
