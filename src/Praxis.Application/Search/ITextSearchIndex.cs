using Praxis.Domain.Search;

namespace Praxis.Application.Search;

/// <summary>Rebuildable full-text index over current extracted text of active sources.</summary>
public interface ITextSearchIndex
{
    /// <summary>Ranked lexical matches for an owner query; operators in the query are treated as text.</summary>
    IReadOnlyList<SearchHit> Query(string query, int limit);

    /// <summary>Rebuilds the index from canonical text within the given bounds; returns the documents indexed.</summary>
    int Rebuild(int maxDocuments, long maxTextBytes);
}
