using Praxis.Domain.Enrichment;
using Praxis.Domain.Understanding;

namespace Praxis.Application.Understanding;

/// <summary>The verified part of one model answer: what passed, what was rejected, and whether that is enough to accept.</summary>
public sealed record UnderstandingDraft(
    string? Summary, string? DocumentType, IReadOnlyList<ProposalCandidate> Candidates, IReadOnlyList<IntakeRecord> Records, int Verified, int Rejected)
{
    /// <summary>The share of items that may fail verification before a stronger tier is asked.</summary>
    public const double TolerableRejection = 0.25;

    public bool Acceptable => Summary is not null && Rejected <= (Verified + Rejected) * TolerableRejection;
}
