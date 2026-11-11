using Praxis.Domain.Processing;

namespace Praxis.Infrastructure.Processing;

/// <summary>
/// Executes one processor's leased jobs. A handler commits its result and terminal job state
/// through the catalog before returning, re-checking the lease so a stale run cannot publish.
/// </summary>
public interface ILocalProcessingHandler
{
    string ProcessorKey { get; }

    /// <summary>Lease length; a run that outlives it loses the right to commit.</summary>
    TimeSpan LeaseDuration { get; }

    /// <summary>Maximum leases this handler holds at once in the background pipeline.</summary>
    int MaxConcurrency { get; }

    Task ProcessAsync(ProcessingLease lease, CancellationToken cancellationToken);

    /// <summary>
    /// Reopens previously closed work that this handler version can now process (for example,
    /// sources earlier marked unsupported). Returns true when it queued at least one job.
    /// </summary>
    bool TryReopenWork();
}
