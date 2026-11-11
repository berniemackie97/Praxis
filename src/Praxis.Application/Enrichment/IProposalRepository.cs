using Praxis.Application.Text;
using Praxis.Domain.Enrichment;
using Praxis.Domain.Processing;

namespace Praxis.Application.Enrichment;

/// <summary>Evidence-bound enrichment proposals, their producer runs and the owner's reviews.</summary>
public interface IProposalRepository
{
    /// <summary>
    /// Records the producer run, its proposals and the job's completion in one transaction, only
    /// while the lease is held, the source is active and the revision digest is unchanged.
    /// </summary>
    bool Commit(ProcessingLease lease, LeasedText input, IReadOnlyList<ProposalCandidate> candidates, ModelRunRecord? model = null);

    /// <summary>The most recent earlier result of <paramref name="producer"/> for the item, for reuse of unchanged segments.</summary>
    PriorExtraction? ReadPrior(Guid itemId, int revision, string producer);

    IReadOnlyList<EnrichmentProposal> List(Guid sourceId, int limit);

    /// <summary>Records an owner decision (accepted, rejected or corrected) on a current proposal.</summary>
    bool Review(Guid proposalId, string decision, string? correction);

    /// <summary>The outcome of <paramref name="producer"/> for the source's current revision.</summary>
    string Outcome(Guid sourceId, string producer);
}
