using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Praxis.Domain.Originals;
using Praxis.Domain.Search;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Support;
using Xunit;
using Xunit.Abstractions;

namespace Praxis.Infrastructure.Tests.Search;

public sealed class RetrievalEvaluationTests(ITestOutputHelper output) : IDisposable
{
    private readonly string root = Path.Combine(TestPaths.Temp,
        "praxis-retrieval-evaluation-" + Guid.NewGuid().ToString("N"));
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task GoldCorpusMeasuresLexicalResultsAndPreservesExactCitations()
    {
        PrepareWorkspace();
        Corpus corpus = ReadCorpus();
        using SqliteCatalog catalog = SqliteCatalog.Open(Path.Combine(root, "catalog"));
        Dictionary<string, CapturedOriginal> originals = await CaptureAsync(catalog, corpus);
        await ProcessAsync(catalog, corpus.Documents.Length);

        Stopwatch clock = Stopwatch.StartNew();
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        Dictionary<string, (int Queries, double Recall, double Precision)> byDomain = new();

        foreach (GoldQuery query in corpus.Queries)
        {
            SearchHit[] hits = catalog.Search.Query(query.Query, 5).ToArray();
            Guid[] expected = query.Expected.Select(key => originals[key].ItemId)
                .OrderBy(id => id).ToArray();
            Guid[] actual = hits.Select(hit => hit.ItemId).OrderBy(id => id).ToArray();
            int found = hits.Count(hit => expected.Contains(hit.ItemId));
            double recall = expected.Length == 0
                ? hits.Length == 0 ? 1d : 0d
                : (double)found / expected.Length;
            double precision = hits.Length == 0
                ? expected.Length == 0 ? 1d : 0d
                : (double)found / hits.Length;
            var score = byDomain.GetValueOrDefault(query.Domain);
            byDomain[query.Domain] = (score.Queries + 1,
                score.Recall + recall, score.Precision + precision);

            Assert.Equal(expected, actual);
            foreach (SearchHit hit in hits)
            {
                CapturedOriginal original = originals.Values.Single(entry => entry.ItemId == hit.ItemId);
                Assert.Equal(original.SourceId, hit.SourceId);
                Assert.Equal(original.Revision, hit.Revision);
                Assert.Equal(original.Sha256, hit.OriginalSha256);
                Assert.EndsWith(hit.Title, hit.SourceLocator, StringComparison.Ordinal);
                Assert.InRange(hit.Snippet.Length, 1, 480);
                Assert.Contains("⟦", hit.Snippet, StringComparison.Ordinal);
            }
        }

        clock.Stop();
        long allocations = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        foreach (KeyValuePair<string, (int Queries, double Recall, double Precision)> entry in
            byDomain.OrderBy(pair => pair.Key))
        {
            var score = entry.Value;
            output.WriteLine($"domain={entry.Key} queries={score.Queries} " +
                $"recall@5={score.Recall / score.Queries:F3} " +
                $"precision@5={score.Precision / score.Queries:F3}");
        }

        output.WriteLine($"fixture=v{corpus.SchemaVersion} queries={corpus.Queries.Length} " +
            $"documents={corpus.Documents.Length} query_elapsed_ms={clock.Elapsed.TotalMilliseconds:F2} " +
            $"query_allocated_bytes={allocations} " +
            $"catalog_bytes={CatalogSize(catalog.DatabasePath)} " +
            $"runtime={RuntimeInformation.FrameworkDescription} os={RuntimeInformation.OSDescription}");

        foreach (GoldDocument document in corpus.Documents)
        {
            CapturedOriginal original = originals[document.Id];
            await catalog.Originals.VerifyAsync(original.ItemId);
            var derivative = catalog.Text.Read(original.ItemId, 1);
            Assert.Equal(document.Searchable ? "completed" : "unsupported", derivative?.Outcome);
        }

        Assert.Equal(corpus.Documents.Count(document => document.Searchable), catalog.Search.Rebuild());
        foreach (GoldQuery query in corpus.Queries)
        {
            Guid[] expected = query.Expected.Select(key => originals[key].ItemId).OrderBy(id => id).ToArray();
            Guid[] actual = catalog.Search.Query(query.Query, 5)
                .Select(hit => hit.ItemId).OrderBy(id => id).ToArray();
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public async Task RevisionsRevocationAndLostIndexCannotExposeStaleEvidence()
    {
        PrepareWorkspace();
        Corpus corpus = ReadCorpus();
        using SqliteCatalog catalog = SqliteCatalog.Open(Path.Combine(root, "catalog"));
        Dictionary<string, CapturedOriginal> original = await CaptureAsync(catalog, corpus);
        await ProcessAsync(catalog, corpus.Documents.Length);
        Assert.Equal(original["mesh"].ItemId, Assert.Single(catalog.Search.Query("ParseMesh")).ItemId);
        Assert.Equal(original["captions"].ItemId, Assert.Single(catalog.Search.Query("vertex buffer")).ItemId);

        CapturedOriginal revised = await CaptureAsync(catalog, "technical/MeshReader.cs",
            "public sealed class MeshReader { public void BuildTriangles() { } }", "mesh-revision");
        Assert.Equal(original["mesh"].SourceId, revised.SourceId);
        Assert.Equal(original["mesh"].ItemId, revised.ItemId);
        Assert.Equal(2, revised.Revision);

        // A new revision is not searchable until its own processing finishes.
        Assert.Empty(catalog.Search.Query("ParseMesh"));
        Assert.Empty(catalog.Search.Query("BuildTriangles"));
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        Assert.Empty(catalog.Search.Query("ParseMesh"));
        Assert.Equal(2, Assert.Single(catalog.Search.Query("BuildTriangles")).Revision);

        Assert.True(catalog.Sources.Revoke(original["captions"].SourceId));
        Assert.Empty(catalog.Search.Query("vertex buffer"));
        await catalog.Originals.VerifyAsync(original["captions"].ItemId);

        // Simulate loss of the derived FTS data, not loss of authoritative originals.
        using (SqliteConnection db = new(new SqliteConnectionStringBuilder
        {
            DataSource = catalog.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString()))
        {
            db.Open();
            using SqliteCommand delete = db.CreateCommand();
            delete.CommandText = "DELETE FROM text_search";
            delete.ExecuteNonQuery();
        }

        Assert.Empty(catalog.Search.Query("BuildTriangles"));
        int expectedDocuments = corpus.Documents.Count(document => document.Searchable) - 1;
        Assert.Equal(expectedDocuments, catalog.Search.Rebuild());
        SearchHit hit = Assert.Single(catalog.Search.Query("BuildTriangles"));
        Assert.Equal(revised.SourceId, hit.SourceId);
        Assert.Equal(revised.ItemId, hit.ItemId);
        Assert.Equal(revised.Revision, hit.Revision);
        Assert.Equal(revised.Sha256, hit.OriginalSha256);
        Assert.Empty(catalog.Search.Query("ParseMesh"));
        Assert.Empty(catalog.Search.Query("vertex buffer"));
        await catalog.Originals.VerifyAsync(revised.ItemId);
    }

    [Fact]
    public async Task FailedRebuildBudgetsDoNotDeletePreviouslyIndexedEvidence()
    {
        PrepareWorkspace();
        Corpus corpus = ReadCorpus();
        using SqliteCatalog catalog = SqliteCatalog.Open(Path.Combine(root, "catalog"));
        Dictionary<string, CapturedOriginal> originals = await CaptureAsync(catalog, corpus);
        await ProcessAsync(catalog, corpus.Documents.Length);

        Assert.Throws<InvalidOperationException>(() => catalog.Search.Rebuild(maxDocuments: 1));
        Assert.Throws<InvalidOperationException>(() => catalog.Search.Rebuild(maxTextBytes: 16));
        Assert.Equal(originals["terrain"].ItemId,
            Assert.Single(catalog.Search.Query("terrain atlas")).ItemId);
        Assert.Equal(2, catalog.Search.Query("orion duplicate").Count);
        Assert.Equal(corpus.Documents.Count(document => document.Searchable),
            catalog.Search.Rebuild());
    }

    private void PrepareWorkspace()
    {
        Directory.CreateDirectory(Path.Combine(root, "catalog"));
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(root, mode);
            File.SetUnixFileMode(Path.Combine(root, "catalog"), mode);
        }
    }

    private static Corpus ReadCorpus()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "retrieval-corpus-v1.json");
        Corpus corpus = JsonSerializer.Deserialize<Corpus>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("The retrieval fixture is empty.");
        Assert.Equal(1, corpus.SchemaVersion);
        Assert.Equal(corpus.Documents.Length, corpus.Documents.Select(document => document.Id).Distinct().Count());
        Assert.All(corpus.Queries, query => Assert.All(query.Expected,
            key => Assert.Contains(corpus.Documents, document => document.Id == key)));
        return corpus;
    }

    private async Task<Dictionary<string, CapturedOriginal>> CaptureAsync(SqliteCatalog catalog, Corpus corpus)
    {
        Dictionary<string, CapturedOriginal> originals = new(StringComparer.Ordinal);
        foreach (GoldDocument document in corpus.Documents)
        {
            originals.Add(document.Id,
                await CaptureAsync(catalog, document.Path, document.Content, document.Id));
        }

        return originals;
    }

    private async Task<CapturedOriginal> CaptureAsync(
        SqliteCatalog catalog, string relativePath, string content, string key)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(content);
        using MemoryStream input = new(bytes, writable: false);
        return await catalog.Originals.CaptureFileAsync(Path.Combine(root, "source", relativePath),
            input, bytes.LongLength, key);
    }

    private static async Task ProcessAsync(SqliteCatalog catalog, int count)
    {
        ProcessingDriver processor = ProcessingDriver.Text(catalog);
        for (int index = 0; index < count; index++)
        {
            Assert.True(await processor.ProcessNextAsync());
        }

        Assert.False(await processor.ProcessNextAsync());
    }

    private static long CatalogSize(string database)
    {
        long size = new FileInfo(database).Length;
        string wal = database + "-wal";
        return size + (File.Exists(wal) ? new FileInfo(wal).Length : 0);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record Corpus(int SchemaVersion, GoldDocument[] Documents, GoldQuery[] Queries);
    private sealed record GoldDocument(string Id, string Path, string Content, bool Searchable);
    private sealed record GoldQuery(string Name, string Domain, string Query, string[] Expected);
}
