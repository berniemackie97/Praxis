namespace Praxis.Application.Knowledge;

/// <summary>Read-only, owner-local projection of current authorized knowledge.</summary>
public interface IGraphReader
{
    GraphOverview Overview(GraphQuery query, CancellationToken cancellationToken = default);

    GraphZoneOverview Zones(GraphQuery query, CancellationToken cancellationToken = default);

    GraphPage Atlas(GraphQuery query, CancellationToken cancellationToken = default);

    GraphPage Region(GraphQuery query, CancellationToken cancellationToken = default);

    GraphPage Seeds(string? search, GraphQuery query, CancellationToken cancellationToken = default);

    GraphPage Neighborhood(Guid focusId, GraphQuery query, CancellationToken cancellationToken = default);

    GraphPage Relation(Guid focusId, Guid relationId, GraphQuery query, CancellationToken cancellationToken = default);
}
