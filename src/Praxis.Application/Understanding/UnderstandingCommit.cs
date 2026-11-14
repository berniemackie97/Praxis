using Praxis.Domain.Enrichment;
using Praxis.Domain.Finance;
using Praxis.Domain.Models;
using Praxis.Domain.Understanding;

namespace Praxis.Application.Understanding;

/// <summary>
/// Everything the understand stage verified for one revision, committed in one transaction:
/// summary, knowledge proposals, typed records, the ledger projection and provenance.
/// </summary>
public sealed record UnderstandingCommit(
    string Status, string? Reason, string? Summary, string? DocumentType,
    string? Tier, string? ProviderId, ModelIdentity? Identity,
    IReadOnlyList<ProposalCandidate> Candidates, IReadOnlyList<IntakeRecord> Records,
    FinancialDocument? Ledger, IReadOnlyList<TransactionAnnotation> Annotations, string? LedgerTier, ModelIdentity? LedgerModel,
    int VerifiedItems, int RejectedItems, int InputTokens, int OutputTokens, string Cascade = "")
{
    public const int MaximumSummaryLength = 1_200;
}
