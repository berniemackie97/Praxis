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
    public async Task<(IReadOnlyList<SearchHit> Hits, string Mode)> SearchAsync(string query, int limit, Guid? collection, CancellationToken cancellationToken)
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
