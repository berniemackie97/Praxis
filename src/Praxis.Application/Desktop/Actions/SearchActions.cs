using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Inference;
using Praxis.Application.Models;
using Praxis.Application.Search;
using Praxis.Domain.Models;
using Praxis.Domain.Search;

namespace Praxis.Application.Desktop.Actions;

/// <summary>
/// Owner search: hybrid lexical and semantic retrieval when local embeddings are enabled and
/// indexed, keyword retrieval otherwise, and on-demand refresh of the semantic index.
/// </summary>
public sealed class SearchActions(
    ITextSearchIndex lexical, ISemanticVectorStore vectors, IModelProfileRepository profiles,
    IEmbeddingModelConnector embeddings) : IDesktopActionGroup
{
    private const int ResultLimit = 30;
    private const int SegmentsPerRefresh = 512;
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(30);

    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.OfAsync("search", SearchAsync),
        DesktopAction.OfAsync("semanticIndex", RefreshAsync)
    ];

    private async Task<SearchResponse> SearchAsync(DesktopRequestFields fields, CancellationToken cancellationToken)
    {
        string query = fields.RequireString("query", 256);
        Guid? collectionId = fields.OptionalGuid("collectionId");
        ModelProfile? profile = profiles.Get(ModelProfile.LocalEmbedding);
        if (profile is not { Enabled: true })
        {
            return new SearchResponse(lexical.RetainCurrent(lexical.Query(query, ResultLimit, collectionId), collectionId), "lexical", null);
        }

        try
        {
            using EmbeddingModelConnection connection = embeddings.Connect(profile, SearchTimeout);
            HybridSearchResults result = await new HybridSearch(lexical, new SemanticRetriever(vectors))
                .QueryAsync(query, connection.Model, ResultLimit, cancellationToken, collectionId);
            return new SearchResponse(result.Hits, result.UsedEmbeddings ? "hybrid" : "lexical",
                result.UsedEmbeddings ? null : "No indexed embeddings for this model. Update the local semantic index to enable hybrid results.");
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
            exception is InferenceException or SemanticSearchCapacityException or InvalidDataException)
        {
            return new SearchResponse(lexical.RetainCurrent(lexical.Query(query, ResultLimit, collectionId), collectionId), "lexical",
                "Semantic retrieval is unavailable or exceeds its index budget; showing current keyword results.");
        }
    }

    private async Task<SemanticIndexResponse> RefreshAsync(DesktopRequestFields fields, CancellationToken cancellationToken)
    {
        ModelProfile profile = profiles.Get(ModelProfile.LocalEmbedding)
            ?? throw new InvalidOperationException("Configure a local embedding model first.");
        if (!profile.Enabled)
        {
            throw new InvalidOperationException("Enable local embeddings before updating the index.");
        }

        using EmbeddingModelConnection connection = embeddings.Connect(profile);
        SemanticIndexUpdate updated = await new SemanticIndexer(vectors).RefreshAsync(
            connection.Model, fields.RequireBoolean("rebuild"), SegmentsPerRefresh, cancellationToken);
        return new SemanticIndexResponse(updated);
    }
}
