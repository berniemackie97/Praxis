using Praxis.Application.Inference;
using Praxis.Application.Models;
using Praxis.Domain.Models;
using Praxis.Domain.Search;

namespace Praxis.Application.Search;

/// <summary>
/// Hybrid keyword + semantic retrieval when a local embedding model is enabled, keyword otherwise.
/// Returns current, active sources only; callers apply the disclosure policy.
/// </summary>
public sealed class KnowledgeRetriever(ITextSearchIndex lexical, ISemanticVectorStore vectors, IModelProfileRepository profiles, IEmbeddingModelConnector embeddings)
{
    /// <summary>
    /// Every query term first; when no source contains them all, any of them, ranked by how many and
    /// how rare (mode <c>partial</c>), so a long question still finds the closest sources.
    /// </summary>
    public async Task<(IReadOnlyList<SearchHit> Hits, string Mode)> SearchAsync(string query, int limit, Guid? collection, CancellationToken cancellationToken)
    {
        (IReadOnlyList<SearchHit> hits, string mode) = await AllTermsAsync(query, limit, collection, cancellationToken);
        return hits.Count > 0 ? (hits, mode) : (lexical.RetainCurrent(lexical.QueryAny(query, limit, collection), collection), "partial");
    }

    public IReadOnlyList<SearchHit> SearchTimedText(string query, int limit, Guid? collection)
    {
        IReadOnlyList<SearchHit> hits = lexical.RetainCurrent(lexical.QueryTimedText(query, limit, collection, anyTerm: false), collection);
        return hits.Count > 0 ? hits : lexical.RetainCurrent(lexical.QueryTimedText(query, limit, collection, anyTerm: true), collection);
    }

    private async Task<(IReadOnlyList<SearchHit> Hits, string Mode)> AllTermsAsync(string query, int limit, Guid? collection, CancellationToken cancellationToken)
    {
        if (profiles.Get(ModelProfile.LocalEmbedding) is { Enabled: true } profile)
        {
            try
            {
                using EmbeddingModelConnection connection = embeddings.Connect(profile, TimeSpan.FromSeconds(20));
                HybridSearchResults hybrid = await new HybridSearch(lexical, new SemanticRetriever(vectors)).QueryAsync(query, connection.Model, limit, cancellationToken, collection);
                return (hybrid.Hits, hybrid.UsedEmbeddings ? "hybrid" : "keyword");
            }
            catch (InferenceException) when (!cancellationToken.IsCancellationRequested)
            {
                // Fall back to keyword search; the mode tells the caller.
            }
        }

        return (lexical.RetainCurrent(lexical.Query(query, limit, collection), collection), "keyword");
    }
}
