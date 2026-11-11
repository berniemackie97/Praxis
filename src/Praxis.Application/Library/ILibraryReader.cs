using Praxis.Domain.Library;

namespace Praxis.Application.Library;

/// <summary>Read model of the latest revision of each item with its text-processing state.</summary>
public interface ILibraryReader
{
    /// <summary>Latest revisions, newest source first, optionally limited to one active collection.</summary>
    IReadOnlyList<LibraryItem> List(int limit, int offset, Guid? collectionId);
}
