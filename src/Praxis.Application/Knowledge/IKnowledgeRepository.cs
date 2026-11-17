using Praxis.Domain.Enrichment;
using Praxis.Domain.Knowledge;

namespace Praxis.Application.Knowledge;

/// <summary>The owner-governed entity and relationship layer built from accepted evidence.</summary>
public interface IKnowledgeRepository
{
    IReadOnlyList<EntitySummary> ListEntities(string? entityType, string? search, int limit, bool includeSuggestions = false);

    EntityDetail? GetEntity(Guid entityId);

    /// <summary>Checks reviewed entity/evidence snapshots and performs one synchronous correction under the catalog writer gate.</summary>
    bool ApplyCurrent(IReadOnlyList<KnowledgeSnapshot> expected, Func<bool> correction);

    bool Rename(Guid entityId, string name);

    bool SetType(Guid entityId, string entityType);

    bool Merge(Guid sourceEntityId, Guid targetEntityId);

    bool Reject(Guid entityId);

    bool Restore(Guid entityId);

    Guid Relate(Guid subjectEntityId, string predicate, Guid objectEntityId);

    /// <summary>Owner decisions about names (confirmed types, rejected names) passed to extraction as hints.</summary>
    IReadOnlyList<EntityHint> GoverningHints(int limit);
}
