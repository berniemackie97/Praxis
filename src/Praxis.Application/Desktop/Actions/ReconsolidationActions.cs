using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Knowledge;
using Praxis.Domain.Knowledge;

namespace Praxis.Application.Desktop.Actions;

/// <summary>Owner view of how Praxis revises its own classifications, and review of its proposals.</summary>
public sealed class ReconsolidationActions(IKnowledgeReconsolidation reconsolidation, RevisionReview review) : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.Of("knowledgeRevisions", fields =>
        {
            string? state = fields.OptionalText("state", 16, "The revision state is invalid.");
            if (state is not null && !RevisionVocabulary.States.Contains(state))
            {
                throw new ArgumentException("The revision state is invalid.");
            }

            return new KnowledgeRevisionsResponse(reconsolidation.Revisions(state, fields.IntegerOrDefault("limit", 100, 1, 500),
                fields.IntegerOrDefault("afterRevisionId", 0, 0, int.MaxValue)));
        }),
        DesktopAction.Of("knowledgeWatermark", _ =>
        {
            (long graphRevision, long latestRevisionId) = reconsolidation.Watermark();
            return new KnowledgeWatermarkResponse(graphRevision, latestRevisionId);
        }),
        DesktopAction.Of("knowledgeRevisionDecide", fields =>
        {
            string decision = fields.RequireString("decision", 16);
            if (decision is not (RevisionVocabulary.Accepted or RevisionVocabulary.Dismissed))
            {
                throw new ArgumentException("The decision must be accepted or dismissed.");
            }

            int revisionId = fields.OptionalInteger("revisionId", 1, int.MaxValue)
                ?? throw new ArgumentException("The revision id is required.");
            return new ChangedResponse(review.Decide(revisionId, decision == RevisionVocabulary.Accepted));
        })
    ];
}
