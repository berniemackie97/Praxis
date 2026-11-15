using Praxis.Domain.Knowledge;

namespace Praxis.Application.Knowledge;

/// <summary>
/// Second looks at entities whose evidence changed. Implementations apply only corrections to
/// model-inferred values and propose everything else; owner and verified decisions are final.
/// </summary>
public interface IKnowledgeReconsolidation
{
    /// <summary>Queues every inferred entity when the classification rules changed since the last run; returns how many.</summary>
    int EnqueueForRuleChange();

    /// <summary>Re-examines up to <paramref name="budget"/> queued entities in one transaction.</summary>
    ReconsolidationPass RunPass(int budget);

    /// <summary>Revisions newest first, optionally only one state and only those after a revision id (for incremental refresh).</summary>
    IReadOnlyList<KnowledgeRevision> Revisions(string? state, int limit, long afterRevisionId = 0);

    /// <summary>The model proposal behind a relation, or null for owner relations.</summary>
    Guid? RelationProposal(Guid relationId);

    /// <summary>The current knowledge revision and newest revision log id; both only grow.</summary>
    (long GraphRevision, long LatestRevisionId) Watermark();

    KnowledgeRevision? Revision(long revisionId);

    /// <summary>Records the owner's verdict on a suggested revision; false when it was not open.</summary>
    bool MarkDecided(long revisionId, bool accepted);
}
