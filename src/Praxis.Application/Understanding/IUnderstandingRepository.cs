using Praxis.Application.Text;
using Praxis.Domain.Processing;
using Praxis.Domain.Understanding;

namespace Praxis.Application.Understanding;

/// <summary>
/// Intake state per revision: how it was read, triaged and understood. Results are committed
/// atomically with the job's terminal state, bound to the revision digest, and visible only
/// while the source is active and the revision current.
/// </summary>
public interface IUnderstandingRepository
{
    /// <summary>The source of a leased triage or understanding job, or null when the lease is not held.</summary>
    LeasedUnderstandingSource? ReadLeasedSource(ProcessingLease lease);

    /// <summary>Stores triage and completes the job, scheduling understanding.</summary>
    bool CommitTriage(ProcessingLease lease, string digest, DocumentProfile profile, string triageTier);

    /// <summary>
    /// Stores the verified understanding and completes the job. A rerun replaces records and the
    /// ledger projection and keeps knowledge proposals already recorded (and their reviews).
    /// </summary>
    bool CommitUnderstanding(ProcessingLease lease, LeasedText input, UnderstandingCommit commit);

    /// <summary>Records why a revision was not understood (held, index-only, skipped, failed) and closes the job.</summary>
    bool CloseUnderstanding(ProcessingLease lease, string digest, string status, string reason, int inputTokens, int outputTokens);

    /// <summary>
    /// Completes the job by copying the understanding of another current revision with the same
    /// digest (a duplicate or re-captured file). Returns false when there is none.
    /// </summary>
    bool TryReuse(ProcessingLease lease, LeasedText input);

    SourceUnderstanding? ReadCurrent(Guid sourceId);

    /// <summary>A cached column mapping for a delimited layout, as JSON, or null.</summary>
    string? ReadLayout(string fingerprint);

    void SaveLayout(string fingerprint, string mapping, string tier, string providerId);
}
