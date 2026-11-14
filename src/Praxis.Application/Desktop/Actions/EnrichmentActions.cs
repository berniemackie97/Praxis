using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Enrichment;
using Praxis.Domain.Processing;

namespace Praxis.Application.Desktop.Actions;

/// <summary>
/// What intake found in a source. Confirmed knowledge needs no review; the owner can remove any of
/// it, and settle the local model's remaining suggestions one by one or all at once.
/// </summary>
public sealed class EnrichmentActions(IProposalRepository proposals) : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.Of("topics", fields =>
        {
            Guid sourceId = fields.RequireGuid("sourceId");
            return new TopicsResponse(
                proposals.List(sourceId, fields.IntegerOrDefault("limit", 40, 1, 100)),
                proposals.Outcome(sourceId, ProcessorKeys.IntakeUnderstand),
                proposals.Outcome(sourceId, ProcessorKeys.SemanticExtraction));
        }),
        DesktopAction.Of("topicReview", fields => new TopicReviewResponse(proposals.Review(
            fields.RequireGuid("proposalId"),
            fields.RequireString("decision", 16),
            fields.OptionalText("correction", 80, "The review correction is invalid.")))),
        DesktopAction.Of("topicReviewAll", fields =>
        {
            Guid sourceId = fields.RequireGuid("sourceId");
            string decision = fields.RequireString("decision", 16);
            if (decision is not ("accepted" or "rejected"))
            {
                throw new ArgumentException("Accept or dismiss all suggestions.");
            }

            int reviewed = proposals.List(sourceId, 100)
                .Where(proposal => !proposal.Confirmed && proposal.Status == "pending")
                .Count(proposal => proposals.Review(proposal.ProposalId, decision, null));
            return new ReviewedCountResponse(reviewed);
        })
    ];
}
