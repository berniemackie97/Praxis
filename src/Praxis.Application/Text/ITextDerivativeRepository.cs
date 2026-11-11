using Praxis.Domain.Processing;
using Praxis.Domain.Text;

namespace Praxis.Application.Text;

/// <summary>
/// Revision- and digest-bound text extracted from immutable originals. A result is committed
/// atomically with its job's terminal state and is readable only while its source is active.
/// </summary>
public interface ITextDerivativeRepository
{
    TextDerivative? Read(Guid itemId, int revision);

    /// <summary>The source of a leased extraction job, or null when the lease is no longer held.</summary>
    LeasedTextSource? ReadLeasedSource(ProcessingLease lease);

    /// <summary>
    /// Completed, digest-matched text of the leased job's revision for a downstream processor,
    /// or null when the lease is no longer held or the source was revoked.
    /// </summary>
    LeasedText? ReadLeasedInput(ProcessingLease lease);

    /// <summary>Stores extracted text or an unsupported reason and finishes the leased job in one transaction.</summary>
    bool CommitResult(ProcessingLease lease, string? content, string? reasonCode);

    /// <summary>
    /// Reopens one active, current source previously classified unsupported only because its
    /// extension had no decoder. Every other recorded outcome remains immutable.
    /// </summary>
    bool RequeueNewlySupported(IReadOnlyList<string> extensions);
}
