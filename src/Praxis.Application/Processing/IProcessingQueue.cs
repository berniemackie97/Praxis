using Praxis.Domain.Processing;

namespace Praxis.Application.Processing;

/// <summary>
/// Durable, leased processing jobs keyed by immutable item revision and processor. Leases fence
/// stale workers; attempt budgets bound retries; only current revisions of active sources are claimable.
/// </summary>
public interface IProcessingQueue
{
    IReadOnlyList<ProcessingJob> List(int limit);

    /// <summary>Current-revision work for active sources; excludes superseded and revoked jobs.</summary>
    ProcessingBacklog Backlog();

    /// <summary>Queues a processor for the current revision of an active source; false when it already exists or is not current.</summary>
    bool Queue(Guid itemId, int revision, string processorKey);

    /// <summary>
    /// Queues a step again for the item's current revision of an active source: a finished job is
    /// reset, a missing one created. Returns false when the job is already queued or running, or
    /// the revision is not current.
    /// </summary>
    bool Requeue(Guid itemId, int revision, string processorKey);

    /// <summary>Leases the highest-priority claimable job, optionally for one processor; expired leases are reclaimable.</summary>
    ProcessingLease? TryClaim(TimeSpan leaseDuration, int maxAttempts, string? processorKey);

    /// <summary>Records a terminal state while the lease is held; a completion schedules dependent processors.</summary>
    bool Finish(ProcessingLease lease, ProcessingCompletion completion, string? failureCode = null);

    /// <summary>
    /// Extends a held lease. Returns null when the lease was lost or the source revoked; when a newer
    /// revision exists the job is canceled instead, so work on superseded content stops.
    /// </summary>
    ProcessingLease? Renew(ProcessingLease lease, TimeSpan extension);

    /// <summary>Returns a held job to the queue and refunds its attempt because a runtime dependency was unavailable.</summary>
    bool Defer(ProcessingLease lease);

    bool Retry(Guid itemId, int revision, string processorKey);

    bool Cancel(Guid itemId, int revision, string processorKey);
}
