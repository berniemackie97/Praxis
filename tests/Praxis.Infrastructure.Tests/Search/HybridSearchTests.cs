using System.Text;
using Microsoft.Data.Sqlite;
using Praxis.Application.Inference;
using Praxis.Application.Search;
using Praxis.Application.Text;
using Praxis.Domain.Models;
using Praxis.Domain.Originals;
using Praxis.Domain.Processing;
using Praxis.Domain.Search;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Search;

public sealed class HybridSearchTests : IDisposable
{
    private readonly string root = Path.Combine(TestPaths.Temp,
        "praxis-hybrid-" + Guid.NewGuid().ToString("N"));
    private readonly string catalogRoot;

    public HybridSearchTests()
    {
        catalogRoot = Path.Combine(root, "catalog");
        Directory.CreateDirectory(catalogRoot);
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(root, privateMode);
            File.SetUnixFileMode(catalogRoot, privateMode);
        }
    }

    [Fact]
    public async Task SemanticRecallFindsConceptWithoutExactKeywordAndPreservesSourceIdentity()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot);
        CapturedOriginal orchard = await Capture(catalog, "orchard.md", "tangerine orchard harvest", "orchard");
        CapturedOriginal ledger = await Capture(catalog, "ledger.md", "invoice ledger balances", "ledger");
        await SegmentNext(catalog);
        await SegmentNext(catalog);

        FixtureEmbeddingModel model = new();
        Assert.Equal(new SemanticIndexUpdate(2, false),
            await new SemanticIndexer(catalog.Embeddings).RefreshAsync(model, rebuild: true));
        Assert.Empty(catalog.Search.Query("citrus"));

        var hybrid = await new HybridSearch(catalog.Search, new SemanticRetriever(catalog.Embeddings)).QueryAsync("citrus", model);
        Assert.True(hybrid.UsedEmbeddings);
        Assert.Equal(orchard.SourceId, hybrid.Hits[0].SourceId);
        Assert.Equal(orchard.ItemId, hybrid.Hits[0].ItemId);
        Assert.Equal(orchard.Sha256, hybrid.Hits[0].OriginalSha256);
        Assert.Equal(ledger.SourceId, hybrid.Hits[1].SourceId);
        Assert.Equal(2, hybrid.Hits.Select(hit => hit.SourceId).Distinct().Count());
        Assert.Contains("tangerine", hybrid.Hits[0].Snippet);

        var lexical = await new HybridSearch(catalog.Search, new SemanticRetriever(catalog.Embeddings)).QueryAsync("invoice", model: null);
        Assert.False(lexical.UsedEmbeddings);
        Assert.Equal(ledger.ItemId, Assert.Single(lexical.Hits).ItemId);
    }

    [Fact]
    public async Task RevokeRevisionAndLostDerivedIndexCannotExposeStaleSource()
    {
        Guid firstSourceId;
        Guid secondSourceId;
        using (SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot))
        {
            CapturedOriginal first = await Capture(catalog, "one.md", "shared tangerine orchard", "one");
            CapturedOriginal second = await Capture(catalog, "two.md", "shared tangerine orchard", "two");
            firstSourceId = first.SourceId;
            secondSourceId = second.SourceId;
            await SegmentNext(catalog);
            await SegmentNext(catalog);
            Assert.Equal(2, (await new SemanticIndexer(catalog.Embeddings).RefreshAsync(new FixtureEmbeddingModel(), rebuild: true)).Indexed);

            Assert.True(catalog.Sources.Revoke(firstSourceId));
            var hits = await new HybridSearch(catalog.Search, new SemanticRetriever(catalog.Embeddings)).QueryAsync("citrus", new FixtureEmbeddingModel());
            Assert.DoesNotContain(hits.Hits, hit => hit.SourceId == firstSourceId);
            Assert.Contains(hits.Hits, hit => hit.SourceId == secondSourceId);
            await catalog.Originals.VerifyAsync(first.ItemId);

            CapturedOriginal revised = await Capture(catalog, "two.md", "invoice ledger balances", "two-revised");
            Assert.Equal(secondSourceId, revised.SourceId);
            Assert.Equal(2, revised.Revision);
            Assert.Empty((await new HybridSearch(catalog.Search, new SemanticRetriever(catalog.Embeddings)).QueryAsync("tangerine")).Hits);
            await SegmentNext(catalog);
            Assert.Empty((await new HybridSearch(catalog.Search, new SemanticRetriever(catalog.Embeddings)).QueryAsync("tangerine")).Hits);
            Assert.Equal(1, (await new SemanticIndexer(catalog.Embeddings).RefreshAsync(new FixtureEmbeddingModel(), rebuild: true)).Indexed);
        }

        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(catalogRoot, "catalog.sqlite"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString()))
        {
            connection.Open();
            using SqliteCommand erase = connection.CreateCommand("DELETE FROM semantic_vectors");
            erase.ExecuteNonQuery();
        }

        using SqliteCatalog reopened = SqliteCatalog.Open(catalogRoot);
        Assert.False((await new HybridSearch(reopened.Search, new SemanticRetriever(reopened.Embeddings)).QueryAsync("citrus", new FixtureEmbeddingModel())).UsedEmbeddings);
        Assert.Equal(1, (await new SemanticIndexer(reopened.Embeddings).RefreshAsync(new FixtureEmbeddingModel(), rebuild: true)).Indexed);
        var rebuilt = await new HybridSearch(reopened.Search, new SemanticRetriever(reopened.Embeddings)).QueryAsync("finances", new FixtureEmbeddingModel());
        Assert.True(rebuilt.UsedEmbeddings);
        Assert.Equal(secondSourceId, Assert.Single(rebuilt.Hits).SourceId);
    }

    [Fact]
    public async Task FailedRebuildOrChangedModelNeverDestroysPreviousIndex()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot);
        await Capture(catalog, "orchard.md", "tangerine orchard harvest", "one");
        await Capture(catalog, "ledger.md", "invoice ledger balances", "two");
        await SegmentNext(catalog);
        await SegmentNext(catalog);
        FixtureEmbeddingModel valid = new();

        Assert.Equal(2, (await new SemanticIndexer(catalog.Embeddings).RefreshAsync(valid, rebuild: true)).Indexed);
        Assert.Throws<InvalidOperationException>(() =>
            new SemanticIndexer(catalog.Embeddings).RefreshAsync(valid, rebuild: true, maxSegments: 1).GetAwaiter().GetResult());
        FixtureEmbeddingModel invalid = new(invalid: true);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new SemanticIndexer(catalog.Embeddings).RefreshAsync(invalid, rebuild: true));

        var preserved = await new HybridSearch(catalog.Search, new SemanticRetriever(catalog.Embeddings)).QueryAsync("citrus", valid);
        Assert.True(preserved.UsedEmbeddings);
        Assert.Equal("orchard.md", preserved.Hits[0].Title);
        Assert.False((await new HybridSearch(catalog.Search, new SemanticRetriever(catalog.Embeddings)).QueryAsync("citrus",
            new FixtureEmbeddingModel(digest: "different-model"))).UsedEmbeddings);
        Assert.Equal(new SemanticIndexUpdate(0, false),
            await new SemanticIndexer(catalog.Embeddings).RefreshAsync(valid));
    }

    [Fact]
    public async Task RevocationWhileIndexingCannotCommitAnUnauthorizedVector()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot);
        CapturedOriginal original = await Capture(catalog, "orchard.md",
            "tangerine orchard harvest", "index-then-revoke");
        await SegmentNext(catalog);
        FixtureEmbeddingModel revoking = new(onIndex: () =>
            Assert.True(catalog.Sources.Revoke(original.SourceId)));

        SemanticIndexUpdate result = await new SemanticIndexer(catalog.Embeddings).RefreshAsync(
            revoking, rebuild: true);
        Assert.Equal(0, result.Indexed);
        Assert.Empty(catalog.Search.Query("tangerine"));
        Assert.False((await new HybridSearch(catalog.Search, new SemanticRetriever(catalog.Embeddings)).QueryAsync("citrus",
            new FixtureEmbeddingModel())).UsedEmbeddings);
        await catalog.Originals.VerifyAsync(original.ItemId);
    }

    [Fact]
    public async Task RevocationWhileEmbeddingQuestionCannotResurrectLexicalHits()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot);
        CapturedOriginal original = await Capture(catalog, "violet.md", "violet tangerine orchard", "one");
        await SegmentNext(catalog);
        await new SemanticIndexer(catalog.Embeddings).RefreshAsync(new FixtureEmbeddingModel(), rebuild: true);
        FixtureEmbeddingModel revoking = new(onQuery: () =>
        {
            Assert.True(catalog.Sources.Revoke(original.SourceId));
        });

        var result = await new HybridSearch(catalog.Search, new SemanticRetriever(catalog.Embeddings)).QueryAsync("violet", revoking);
        Assert.Empty(result.Hits);
        Assert.False(result.UsedEmbeddings);
        Assert.Empty(catalog.Search.Query("violet"));
        await catalog.Originals.VerifyAsync(original.ItemId);
    }

    [Fact]
    public async Task RevokingAfterBothRetrievalListsAreReadCannotExposePreviouslyRankedEvidence()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot);
        CapturedOriginal revoked = await Capture(catalog, "revoked.md",
            "tangerine orchard harvest", "revoked");
        CapturedOriginal retained = await Capture(catalog, "retained.md",
            "invoice ledger balances", "retained");
        await SegmentNext(catalog);
        await SegmentNext(catalog);
        FixtureEmbeddingModel model = new();
        Assert.Equal(2, (await new SemanticIndexer(catalog.Embeddings)
            .RefreshAsync(model, rebuild: true)).Indexed);

        ITextSearchIndex index = new RevokingTextIndex(catalog.Search,
            () => Assert.True(catalog.Sources.Revoke(revoked.SourceId)));
        HybridSearchResults result = await new HybridSearch(
            index, new SemanticRetriever(catalog.Embeddings)).QueryAsync("citrus", model);

        Assert.True(result.UsedEmbeddings);
        Assert.DoesNotContain(result.Hits, hit => hit.SourceId == revoked.SourceId);
        Assert.Equal(retained.ItemId, Assert.Single(result.Hits).ItemId);
        Assert.Empty(catalog.Search.Query("tangerine"));
    }

    [Fact]
    public async Task InvalidatedSemanticCandidatesDoNotReportEmbeddingsOrLeakSnippets()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot);
        CapturedOriginal source = await Capture(catalog, "private.md",
            "tangerine orchard secret", "one");
        await SegmentNext(catalog);
        FixtureEmbeddingModel model = new();
        Assert.Equal(1, (await new SemanticIndexer(catalog.Embeddings)
            .RefreshAsync(model, rebuild: true)).Indexed);

        ITextSearchIndex index = new RevokingTextIndex(catalog.Search,
            () => Assert.True(catalog.Sources.Revoke(source.SourceId)));
        HybridSearchResults result = await new HybridSearch(
            index, new SemanticRetriever(catalog.Embeddings)).QueryAsync("citrus", model);

        Assert.False(result.UsedEmbeddings);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task LexicalOnlyResultIsRevalidatedAfterItsInitialRead()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot);
        CapturedOriginal source = await Capture(catalog, "local.md",
            "violet private notes", "one");
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());

        ITextSearchIndex index = new RevokingTextIndex(catalog.Search,
            () => Assert.True(catalog.Sources.Revoke(source.SourceId)));
        HybridSearchResults result = await new HybridSearch(
            index, new SemanticRetriever(catalog.Embeddings)).QueryAsync("violet");

        Assert.False(result.UsedEmbeddings);
        Assert.Empty(result.Hits);
    }

    [Fact]
    public async Task CollectionScopedSearchFiltersBothRetrievalModesAndLateMembershipChanges()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot);
        CapturedOriginal approved = await Capture(catalog, "approved.md",
            "tangerine orchard protocol", "approved");
        CapturedOriginal outside = await Capture(catalog, "outside.md",
            "tangerine orchard private", "outside");
        await SegmentNext(catalog);
        await SegmentNext(catalog);

        Guid collectionId = catalog.Collections.Create("Reviewed sources").CollectionId;
        Assert.True(catalog.Collections.AddSource(collectionId, approved.SourceId));
        FixtureEmbeddingModel model = new();
        Assert.Equal(2, (await new SemanticIndexer(catalog.Embeddings)
            .RefreshAsync(model, rebuild: true)).Indexed);

        HybridSearch search = new(catalog.Search, new SemanticRetriever(catalog.Embeddings));
        HybridSearchResults all = await search.QueryAsync("citrus", model);
        Assert.Equal(2, all.Hits.Count);

        HybridSearchResults scoped = await search.QueryAsync(
            "citrus", model, 20, default, collectionId);
        Assert.True(scoped.UsedEmbeddings);
        Assert.Equal(approved.SourceId, Assert.Single(scoped.Hits).SourceId);

        SearchHit keyword = Assert.Single(catalog.Search.Query("tangerine", 20, collectionId));
        Assert.Equal(approved.SourceId, keyword.SourceId);
        Assert.Single(catalog.Search.RetainCurrent([keyword], collectionId));

        Assert.True(catalog.Collections.RemoveSource(collectionId, approved.SourceId));
        Assert.Empty(catalog.Search.RetainCurrent([keyword], collectionId));
        Assert.Empty(catalog.Search.Query("tangerine", 20, collectionId));
        HybridSearchResults removed = await search.QueryAsync(
            "citrus", model, 20, default, collectionId);
        Assert.False(removed.UsedEmbeddings);
        Assert.Empty(removed.Hits);

        Assert.True(catalog.Collections.AddSource(collectionId, outside.SourceId));
        HybridSearchResults reassigned = await search.QueryAsync(
            "citrus", model, 20, default, collectionId);
        Assert.Equal(outside.SourceId, Assert.Single(reassigned.Hits).SourceId);

        Assert.True(catalog.Collections.Archive(collectionId));
        Assert.Empty(catalog.Search.Query("tangerine", 20, collectionId));
        Assert.Empty((await search.QueryAsync("citrus", model, 20, default, collectionId)).Hits);
        Assert.Equal(2, (await search.QueryAsync("citrus", model)).Hits.Count);
    }

    [Fact]
    public async Task CollectionRemovalAfterCandidateReadsRetractsHybridSnippets()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(catalogRoot);
        CapturedOriginal saved = await Capture(catalog, "scope.md",
            "tangerine orchard harvest", "one");
        await SegmentNext(catalog);
        Guid collectionId = catalog.Collections.Create("Restricted").CollectionId;
        Assert.True(catalog.Collections.AddSource(collectionId, saved.SourceId));
        FixtureEmbeddingModel model = new();
        Assert.Equal(1, (await new SemanticIndexer(catalog.Embeddings)
            .RefreshAsync(model, rebuild: true)).Indexed);

        ITextSearchIndex index = new RevokingTextIndex(catalog.Search,
            () => Assert.True(catalog.Collections.RemoveSource(collectionId, saved.SourceId)));
        HybridSearchResults result = await new HybridSearch(
            index, new SemanticRetriever(catalog.Embeddings)).QueryAsync(
            "citrus", model, 20, default, collectionId);

        Assert.False(result.UsedEmbeddings);
        Assert.Empty(result.Hits);
        Assert.Empty(catalog.Search.Query("tangerine", 20, collectionId));
    }

    private async Task<CapturedOriginal> Capture(SqliteCatalog catalog, string name, string content, string request)
    {
        using MemoryStream bytes = new(Encoding.UTF8.GetBytes(content));
        return await catalog.Originals.CaptureFileAsync(
            Path.Combine(root, name), bytes, bytes.Length, request);
    }

    private static async Task SegmentNext(SqliteCatalog catalog)
    {
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        ProcessingLease lease = Assert.IsType<ProcessingLease>(catalog.Processing.TryClaim(
            TimeSpan.FromMinutes(1), processorKey: ProcessorKeys.TextSegments));
        await new TextSegmentationHandler(catalog.Text, catalog.Segments, catalog.Processing).ProcessAsync(lease, CancellationToken.None);
        Assert.Equal("completed", Assert.Single(catalog.Processing.List(),
            job => job.ItemId == lease.ItemId && job.Revision == lease.Revision &&
                job.ProcessorKey == ProcessorKeys.TextSegments).State);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RevokingTextIndex(ITextSearchIndex inner, Action afterQuery) : ITextSearchIndex
    {
        public IReadOnlyList<SearchHit> Query(string query, int limit, Guid? collectionId = null)
        {
            IReadOnlyList<SearchHit> results = inner.Query(query, limit, collectionId);
            afterQuery();
            return results;
        }

        public IReadOnlyList<SearchHit> RetainCurrent(IReadOnlyList<SearchHit> candidates,
            Guid? collectionId = null) => inner.RetainCurrent(candidates, collectionId);

        public int Rebuild(int maxDocuments, long maxTextBytes) =>
            inner.Rebuild(maxDocuments, maxTextBytes);
    }

    private sealed class FixtureEmbeddingModel(
        string digest = "fixture-embedding-v1", bool invalid = false,
        Action? onQuery = null, Action? onIndex = null) : IEmbeddingModel
    {
        public Task<ModelIdentity> DescribeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ModelIdentity("fixture", "fixture", digest, null, null, null));

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> inputs, CancellationToken cancellationToken)
        {
            if (inputs.Count == 1 && inputs[0] == "violet")
            {
                onQuery?.Invoke();
            }

            if (inputs.Any(value => value.Contains("orchard", StringComparison.OrdinalIgnoreCase)))
            {
                onIndex?.Invoke();
            }

            IReadOnlyList<float[]> result = inputs.Select(text =>
                invalid ? new[] { float.NaN, 0f } :
                text.Contains("orchard", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("citrus", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("violet", StringComparison.OrdinalIgnoreCase)
                    ? new[] { 1f, 0f }
                    : new[] { 0f, 1f }).ToArray();
            return Task.FromResult(result);
        }
    }
}
