namespace Praxis.Application.Knowledge;

/// <summary>Read-only, owner-local projection of current authorized knowledge.</summary>
public interface IGraphReader
{
    GraphPage Seeds(string? search, GraphQuery query, CancellationToken cancellationToken = default);

    GraphPage Neighborhood(Guid focusId, GraphQuery query, CancellationToken cancellationToken = default);

    GraphPage Relation(Guid focusId, Guid relationId, GraphQuery query, CancellationToken cancellationToken = default);
}
