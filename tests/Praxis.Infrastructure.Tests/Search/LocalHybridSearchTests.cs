using System.Text;
using Microsoft.Data.Sqlite;
using Praxis.Application.Inference;
using Praxis.Domain.Models;
using Praxis.Domain.Originals;
using Praxis.Domain.Processing;
using Praxis.Domain.Search;
using Praxis.Infrastructure.Catalog;
using Praxis.Infrastructure.Processing;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Search;

public sealed class LocalHybridSearchTests : IDisposable
{
    private readonly string root = Path.Combine(TestPaths.Temp,
        "praxis-hybrid-" + Guid.NewGuid().ToString("N"));
    private readonly string catalogRoot;

    public LocalHybridSearchTests()
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
        using LocalCatalog catalog = LocalCatalog.Open(catalogRoot);
        CapturedOriginal orchard = await Capture(catalog, "orchard.md", "tangerine orchard harvest", "orchard");
        CapturedOriginal ledger = await Capture(catalog, "ledger.md", "invoice ledger balances", "ledger");
        await SegmentNext(catalog);
        await SegmentNext(catalog);

        FixtureEmbeddingModel model = new();
        Assert.Equal(new SemanticIndexUpdate(2, false),
            await catalog.Embeddings.RefreshAsync(model, rebuild: true));
        Assert.Empty(catalog.Search.Query("citrus"));

        var hybrid = await catalog.Hybrid.QueryAsync("citrus", model);
        Assert.True(hybrid.UsedEmbeddings);
        Assert.Equal(orchard.SourceId, hybrid.Hits[0].SourceId);
        Assert.Equal(orchard.ItemId, hybrid.Hits[0].ItemId);
        Assert.Equal(orchard.Sha256, hybrid.Hits[0].OriginalSha256);
        Assert.Equal(ledger.SourceId, hybrid.Hits[1].SourceId);
        Assert.Equal(2, hybrid.Hits.Select(hit => hit.SourceId).Distinct().Count());
        Assert.Contains("tangerine", hybrid.Hits[0].Snippet);

        var lexical = await catalog.Hybrid.QueryAsync("invoice", model: null);
        Assert.False(lexical.UsedEmbeddings);
        Assert.Equal(ledger.ItemId, Assert.Single(lexical.Hits).ItemId);
    }

    [Fact]
    public async Task RevokeRevisionAndLostDerivedIndexCannotExposeStaleSource()
    {
        Guid firstSourceId;
        Guid secondSourceId;
        using (LocalCatalog catalog = LocalCatalog.Open(catalogRoot))
        {
            CapturedOriginal first = await Capture(catalog, "one.md", "shared tangerine orchard", "one");
            CapturedOriginal second = await Capture(catalog, "two.md", "shared tangerine orchard", "two");
            firstSourceId = first.SourceId;
            secondSourceId = second.SourceId;
            await SegmentNext(catalog);
            await SegmentNext(catalog);
            Assert.Equal(2, (await catalog.Embeddings.RefreshAsync(new FixtureEmbeddingModel(), rebuild: true)).Indexed);

            Assert.True(catalog.Sources.Revoke(firstSourceId));
            var hits = await catalog.Hybrid.QueryAsync("citrus", new FixtureEmbeddingModel());
            Assert.DoesNotContain(hits.Hits, hit => hit.SourceId == firstSourceId);
            Assert.Contains(hits.Hits, hit => hit.SourceId == secondSourceId);
            await catalog.Originals.VerifyAsync(first.ItemId);

            CapturedOriginal revised = await Capture(catalog, "two.md", "invoice ledger balances", "two-revised");
            Assert.Equal(secondSourceId, revised.SourceId);
            Assert.Equal(2, revised.Revision);
            Assert.Empty((await catalog.Hybrid.QueryAsync("tangerine")).Hits);
            await SegmentNext(catalog);
            Assert.Empty((await catalog.Hybrid.QueryAsync("tangerine")).Hits);
            Assert.Equal(1, (await catalog.Embeddings.RefreshAsync(new FixtureEmbeddingModel(), rebuild: true)).Indexed);
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

        using LocalCatalog reopened = LocalCatalog.Open(catalogRoot);
        Assert.False((await reopened.Hybrid.QueryAsync("citrus", new FixtureEmbeddingModel())).UsedEmbeddings);
        Assert.Equal(1, (await reopened.Embeddings.RefreshAsync(new FixtureEmbeddingModel(), rebuild: true)).Indexed);
        var rebuilt = await reopened.Hybrid.QueryAsync("finances", new FixtureEmbeddingModel());
        Assert.True(rebuilt.UsedEmbeddings);
        Assert.Equal(secondSourceId, Assert.Single(rebuilt.Hits).SourceId);
    }

    [Fact]
    public async Task FailedRebuildOrChangedModelNeverDestroysPreviousIndex()
    {
        using LocalCatalog catalog = LocalCatalog.Open(catalogRoot);
        await Capture(catalog, "orchard.md", "tangerine orchard harvest", "one");
        await Capture(catalog, "ledger.md", "invoice ledger balances", "two");
        await SegmentNext(catalog);
        await SegmentNext(catalog);
        FixtureEmbeddingModel valid = new();

        Assert.Equal(2, (await catalog.Embeddings.RefreshAsync(valid, rebuild: true)).Indexed);
        Assert.Throws<InvalidOperationException>(() =>
            catalog.Embeddings.RefreshAsync(valid, rebuild: true, maxSegments: 1).GetAwaiter().GetResult());
        FixtureEmbeddingModel invalid = new(invalid: true);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.Embeddings.RefreshAsync(invalid, rebuild: true));

        var preserved = await catalog.Hybrid.QueryAsync("citrus", valid);
        Assert.True(preserved.UsedEmbeddings);
        Assert.Equal("orchard.md", preserved.Hits[0].Title);
        Assert.False((await catalog.Hybrid.QueryAsync("citrus",
            new FixtureEmbeddingModel(digest: "different-model"))).UsedEmbeddings);
        Assert.Equal(new SemanticIndexUpdate(0, false),
            await catalog.Embeddings.RefreshAsync(valid));
    }

    [Fact]
    public async Task RevocationWhileIndexingCannotCommitAnUnauthorizedVector()
    {
        using LocalCatalog catalog = LocalCatalog.Open(catalogRoot);
        CapturedOriginal original = await Capture(catalog, "orchard.md",
            "tangerine orchard harvest", "index-then-revoke");
        await SegmentNext(catalog);
        FixtureEmbeddingModel revoking = new(onIndex: () =>
            Assert.True(catalog.Sources.Revoke(original.SourceId)));

        SemanticIndexUpdate result = await catalog.Embeddings.RefreshAsync(
            revoking, rebuild: true);
        Assert.Equal(0, result.Indexed);
        Assert.Empty(catalog.Search.Query("tangerine"));
        Assert.False((await catalog.Hybrid.QueryAsync("citrus",
            new FixtureEmbeddingModel())).UsedEmbeddings);
        await catalog.Originals.VerifyAsync(original.ItemId);
    }

    [Fact]
    public async Task RevocationWhileEmbeddingQuestionCannotResurrectLexicalHits()
    {
        using LocalCatalog catalog = LocalCatalog.Open(catalogRoot);
        CapturedOriginal original = await Capture(catalog, "violet.md", "violet tangerine orchard", "one");
        await SegmentNext(catalog);
        await catalog.Embeddings.RefreshAsync(new FixtureEmbeddingModel(), rebuild: true);
        FixtureEmbeddingModel revoking = new(onQuery: () =>
        {
            Assert.True(catalog.Sources.Revoke(original.SourceId));
        });

        var result = await catalog.Hybrid.QueryAsync("violet", revoking);
        Assert.Empty(result.Hits);
        Assert.False(result.UsedEmbeddings);
        Assert.Empty(catalog.Search.Query("violet"));
        await catalog.Originals.VerifyAsync(original.ItemId);
    }

    private async Task<CapturedOriginal> Capture(LocalCatalog catalog, string name, string content, string request)
    {
        using MemoryStream bytes = new(Encoding.UTF8.GetBytes(content));
        return await catalog.Originals.CaptureFileAsync(
            Path.Combine(root, name), bytes, bytes.Length, request);
    }

    private static async Task SegmentNext(LocalCatalog catalog)
    {
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        ProcessingLease lease = Assert.IsType<ProcessingLease>(catalog.Processing.TryClaim(
            TimeSpan.FromMinutes(1), processorKey: LocalProcessors.TextSegments));
        await new LocalTextSegmenter(catalog).ProcessAsync(lease, CancellationToken.None);
        Assert.Equal("completed", Assert.Single(catalog.Processing.List(),
            job => job.ItemId == lease.ItemId && job.Revision == lease.Revision &&
                job.ProcessorKey == LocalProcessors.TextSegments).State);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
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
