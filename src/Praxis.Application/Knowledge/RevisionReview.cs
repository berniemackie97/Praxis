using Praxis.Application.Enrichment;
using Praxis.Domain.Knowledge;

namespace Praxis.Application.Knowledge;

/// <summary>
/// The owner's verdict on a proposed revision. Accepting applies it as the owner's own decision: a type
/// (which then teaches look-alikes), a merge, or rejecting the model proposal behind a relation.
/// Dismissing keeps it from being proposed again.
/// </summary>
public sealed class RevisionReview(IKnowledgeReconsolidation reconsolidation, IKnowledgeRepository knowledge, IProposalRepository proposals)
{
    public bool Decide(long revisionId, bool accept)
    {
        KnowledgeRevision revision = reconsolidation.Revision(revisionId)
            ?? throw new ArgumentException("The revision does not exist.", nameof(revisionId));
        if (revision.State != RevisionVocabulary.Suggested)
        {
            return false;
        }

        if (accept && !Apply(revision))
        {
            return false;
        }

        return reconsolidation.MarkDecided(revisionId, accept);
    }

    private bool Apply(KnowledgeRevision revision) => revision.Field switch
    {
        RevisionVocabulary.EntityType => knowledge.SetType(revision.EntityId, revision.After),
        RevisionVocabulary.Merge => knowledge.Merge(revision.EntityId, Guid.Parse(revision.After)),
        RevisionVocabulary.Relation => revision.RelationId is Guid relation &&
                                       reconsolidation.RelationProposal(relation) is Guid proposal &&
                                       proposals.Review(proposal, "rejected", null),
        _ => false
    };
}
