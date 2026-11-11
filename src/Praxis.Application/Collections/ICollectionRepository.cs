using Praxis.Domain.Collections;

namespace Praxis.Application.Collections;

/// <summary>Owner-authored collections and their source memberships.</summary>
public interface ICollectionRepository
{
    SourceCollection Create(string name);

    bool Rename(Guid collectionId, string name);

    bool Archive(Guid collectionId);

    IReadOnlyList<SourceCollection> List(int limit, int offset);

    IReadOnlyList<Guid> ListForSource(Guid sourceId, int limit);

    bool AddSource(Guid collectionId, Guid sourceId);

    bool RemoveSource(Guid collectionId, Guid sourceId);

    /// <summary>Whether an active source currently belongs to an active collection.</summary>
    bool ContainsActiveSource(Guid collectionId, Guid sourceId);
}
