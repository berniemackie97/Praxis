using Praxis.Domain.Search;

namespace Praxis.Application.Search;

/// <summary>Rebuildable full-text index over current extracted text of active sources.</summary>
public interface ITextSearchIndex
{
    /// <summary>Ranked lexical matches for an owner query; operators in the query are treated as text.</summary>
    IReadOnlyList<SearchHit> Query(string query, int limit, Guid? collectionId = null);

    /// <summary>
    /// Ranked lexical matches containing any of the query's terms, best first: the fallback when no
    /// source contains them all, such as a long question where some words never appear.
    /// </summary>
    IReadOnlyList<SearchHit> QueryAny(string query, int limit, Guid? collectionId = null);

    /// <summary>Returns document body excerpts, even when a match comes from a source name.</summary>
    IReadOnlyList<SearchHit> QueryPassages(string query, int limit, bool anyTerm = false) =>
        anyTerm ? QueryAny(query, limit) : Query(query, limit);

    /// <summary>Rebuilds the index from canonical text within the given bounds; returns the documents indexed.</summary>
    int Rebuild(int maxDocuments, long maxTextBytes);

    /// <summary>
    /// Discards any retrieved hits whose source, item revision, original digest or locator
    /// is no longer current. The canonical eligibility check executes as one bounded read.
    /// Candidates are not a permission grant or evidence that an answer is supported.
    /// </summary>
    IReadOnlyList<SearchHit> RetainCurrent(IReadOnlyList<SearchHit> candidates, Guid? collectionId = null);
}
