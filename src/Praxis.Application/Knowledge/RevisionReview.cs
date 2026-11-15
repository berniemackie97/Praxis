using Praxis.Domain.Knowledge;

namespace Praxis.Application.Knowledge;

/// <summary>
/// The owner's verdict on a proposed revision. Accepting applies it as an owner type decision,
/// which in turn becomes a lesson for similar entities; dismissing keeps it from being proposed again.
/// </summary>
public sealed class RevisionReview(IKnowledgeReconsolidation reconsolidation, IKnowledgeRepository knowledge)
{
    public bool Decide(long revisionId, bool accept)
    {
        KnowledgeRevision revision = reconsolidation.Revision(revisionId)
            ?? throw new ArgumentException("The revision does not exist.", nameof(revisionId));
        if (revision.State != RevisionVocabulary.Suggested)
        {
            return false;
        }

        if (accept && !knowledge.SetType(revision.EntityId, revision.After))
        {
            return false;
        }

        return reconsolidation.MarkDecided(revisionId, accept);
    }
}
