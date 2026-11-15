namespace Praxis.Application.Knowledge;

/// <summary>Read-only, owner-local projection of current authorized knowledge.</summary>
public interface IGraphReader
{
    GraphOverview Overview(GraphQuery query, CancellationToken cancellationToken = default);

    GraphZoneOverview Zones(GraphQuery query, CancellationToken cancellationToken = default);

    GraphConceptPage ConceptRegions(GraphQuery query, CancellationToken cancellationToken = default,
        Guid? parentRegionId = null);

    /// <summary>The brain's regions from the owner's folders and repositories, with hubs and cross-region evidence.</summary>
    GraphRegionMap KnowledgeRegions(GraphQuery query, CancellationToken cancellationToken = default);

    /// <summary>A page of a region's entities (best connected first) and the confirmed relations among them.</summary>
    GraphPage RegionMembers(string regionId, GraphQuery query, CancellationToken cancellationToken = default);

    GraphClassificationAuditPage ClassificationAudit(GraphQuery query, CancellationToken cancellationToken = default);

    GraphPage Atlas(GraphQuery query, CancellationToken cancellationToken = default);

    GraphPage Region(GraphQuery query, CancellationToken cancellationToken = default);

    GraphPage Seeds(string? search, GraphQuery query, CancellationToken cancellationToken = default);

    GraphPage Neighborhood(Guid focusId, GraphQuery query, CancellationToken cancellationToken = default);

    GraphPage Relation(Guid focusId, Guid relationId, GraphQuery query, CancellationToken cancellationToken = default);
}
