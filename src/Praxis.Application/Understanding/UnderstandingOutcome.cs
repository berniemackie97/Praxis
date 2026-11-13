using Praxis.Application.Policy;
using Praxis.Domain.Enrichment;
using Praxis.Domain.Models;
using Praxis.Domain.Understanding;

namespace Praxis.Application.Understanding;

/// <summary>The merged, verified knowledge understanding of a document and how it was reached.</summary>
public sealed record UnderstandingOutcome(
    string Status, string Reason, string? Summary, string? DocumentType,
    IReadOnlyList<ProposalCandidate> Candidates, IReadOnlyList<IntakeRecord> Records, int Verified, int Rejected,
    string? Tier, string? ProviderId, ModelIdentity? Identity, int InputTokens, int OutputTokens, bool Complete)
{
    public bool HasResult => Status is LadderResult<UnderstandingDraft>.Accepted or LadderResult<UnderstandingDraft>.Partial;
}
