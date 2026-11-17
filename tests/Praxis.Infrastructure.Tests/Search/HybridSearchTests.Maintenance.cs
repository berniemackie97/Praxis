using Praxis.Application.Inference;
using Praxis.Application.Search;
using Praxis.Domain.Models;
using Praxis.Domain.Search;
using Praxis.Infrastructure.Persistence;
using Xunit;

namespace Praxis.Infrastructure.Tests.Search;

public sealed partial class HybridSearchTests
{
    [Fact]
    public async Task BackgroundSemanticUpkeepIndexesNewRevisionsAndSkipsIdleInference()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot);
        await Capture(catalog, "orchard.md", "tangerine orchard harvest", "first");
        await SegmentNext(catalog);
        ModelProfile profile = catalog.Models.Save(new(ModelProfile.LocalEmbedding, "ollama", "http://127.0.0.1:11434", "fixture", 2_048, true));
        CountingEmbeddingConnector connector = new(new FixtureEmbeddingModel());
        SemanticIndexMaintenance maintenance = new(catalog.Embeddings, catalog.Models, connector, TimeProvider.System);
        Assert.Equal(1, await maintenance.RunNextAsync(CancellationToken.None));
        Assert.Equal(0, await maintenance.RunNextAsync(CancellationToken.None));
        Assert.Equal(1, connector.Connections);
        await Capture(catalog, "orchard.md", "invoice ledger balances", "revised");
        await SegmentNext(catalog);
        Assert.Equal(1, await maintenance.RunNextAsync(CancellationToken.None));
        Assert.Equal(2, connector.Connections);
        catalog.Models.Save(profile with { Enabled = false });
        Assert.Equal(0, await maintenance.RunNextAsync(CancellationToken.None));
        Assert.Equal(2, connector.Connections);
    }

    [Fact]
    public async Task BackgroundSemanticUpkeepDiscardsWorkWhenProfileChangesDuringInference()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot);
        await Capture(catalog, "orchard.md", "tangerine orchard harvest", "first");
        await SegmentNext(catalog);
        ModelProfile profile = catalog.Models.Save(new(ModelProfile.LocalEmbedding, "ollama", "http://127.0.0.1:11434", "fixture", 2_048, true));
        CountingEmbeddingConnector connector = new(new FixtureEmbeddingModel(onIndex: () => catalog.Models.Save(profile with { Enabled = false })));
        SemanticIndexMaintenance maintenance = new(catalog.Embeddings, catalog.Models, connector, TimeProvider.System);
        Assert.Equal(0, await maintenance.RunNextAsync(CancellationToken.None));
        Assert.False(catalog.Embeddings.HasVectors("fixture-embedding-v1"));
    }

    [Fact]
    public async Task OversizedSemanticIndexFallsBackToCurrentKeywordEvidence()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot);
        var source = await Capture(catalog, "ledger.md", "invoice ledger balances", "ledger");
        await SegmentNext(catalog);
        catalog.Models.Save(new(ModelProfile.LocalEmbedding, "ollama", "http://127.0.0.1:11434", "fixture", 2_048, true));
        await new SemanticIndexer(catalog.Embeddings).RefreshAsync(new FixtureEmbeddingModel());
        KnowledgeRetriever retriever = new(catalog.Search, new CapacityExceededStore(catalog.Embeddings), catalog.Models,
            new CountingEmbeddingConnector(new FixtureEmbeddingModel(onQuery: () => throw new InvalidOperationException("No query embedding may run beyond capacity."))));
        var (hits, mode) = await retriever.SearchAsync("invoice", 10, null, CancellationToken.None);
        Assert.Equal("keyword", mode);
        Assert.Equal(source.SourceId, Assert.Single(hits).SourceId);
    }

    private sealed class CountingEmbeddingConnector(IEmbeddingModel model) : IEmbeddingModelConnector
    {
        public int Connections { get; private set; }
        public EmbeddingModelConnection Connect(ModelProfile profile, TimeSpan? requestTimeout = null)
        {
            Connections++;
            return new EmbeddingModelConnection(model);
        }
    }

    private sealed class CapacityExceededStore(ISemanticVectorStore inner) : ISemanticVectorStore
    {
        public IReadOnlyList<SemanticSegment> ReadSegmentsToIndex(string digest, bool rebuild, int limit) => inner.ReadSegmentsToIndex(digest, rebuild, limit);
        public int Store(string digest, bool rebuild, IReadOnlyList<SegmentEmbedding> embeddings) => inner.Store(digest, rebuild, embeddings);
        public bool HasVectors(string digest, Guid? collectionId = null) => inner.HasVectors(digest, collectionId);
        public int CountVectors(string digest, int limit, Guid? collectionId = null) => limit;
        public IReadOnlyList<SemanticSearchHit> FindNearest(string digest, float[] query, int limit, int maximumScanned,
            CancellationToken cancellationToken, Guid? collectionId = null) => throw new SemanticSearchCapacityException();
    }
}
