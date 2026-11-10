using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Praxis.Storage.Catalog;
using Praxis.Storage.Originals;
using Praxis.Storage.Processing;
using Praxis.Storage.Search;
using Praxis.Storage.Tests.Support;
using Xunit;

namespace Praxis.Storage.Tests.Search;

internal sealed record RetrievalBenchmarkCorpus(int SchemaVersion, RetrievalBenchmarkDocument[] Documents);
internal sealed record RetrievalBenchmarkDocument(string Id, string Path, string Content, bool Searchable);
internal sealed record RetrievalJudgmentSet(int SchemaVersion, string Corpus, RetrievalJudgment[] Judgments);
internal sealed record RetrievalJudgment(string Name, string Domain, string Query,
    string[] Relevant, string[] LexicalExpected);

internal readonly record struct RetrievalScore(double Recall, double Precision, bool NoAnswerCorrect)
{
    public static RetrievalScore AtFive(IReadOnlyCollection<Guid> relevant, IReadOnlyList<Guid> actual)
    {
        Guid[] returned = actual.Take(5).ToArray();
        if (returned.Length != returned.Distinct().Count())
        {
            throw new InvalidDataException("Retrieval duplicated a canonical item.");
        }

        int found = returned.Count(id => relevant.Contains(id));
        return new RetrievalScore(
            relevant.Count == 0 ? returned.Length == 0 ? 1 : 0 : (double)found / relevant.Count,
            returned.Length == 0 ? relevant.Count == 0 ? 1 : 0 : (double)found / returned.Length,
            relevant.Count == 0 && returned.Length == 0);
    }
}

/// <summary>
/// Executes the shipping capture, extraction, segmentation and search stores on synthetic originals.
/// Never opens the owner's private catalog.
/// </summary>
internal sealed class RetrievalBenchmarkFixture : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string workspace;

    private RetrievalBenchmarkFixture(LocalCatalog catalog,
        RetrievalJudgmentSet judgments, Dictionary<string, CapturedOriginal> originals, string workspace)
    {
        Catalog = catalog;
        Judgments = judgments;
        Originals = originals;
        this.workspace = workspace;
    }

    public LocalCatalog Catalog { get; }
    public RetrievalJudgmentSet Judgments { get; }
    public IReadOnlyDictionary<string, CapturedOriginal> Originals { get; }
    public string RuntimeDescription => RuntimeInformation.FrameworkDescription + " / " +
        RuntimeInformation.OSDescription;

    public static async Task<RetrievalBenchmarkFixture> CreateAsync()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        RetrievalBenchmarkCorpus corpus = JsonSerializer.Deserialize<RetrievalBenchmarkCorpus>(
            File.ReadAllText(Path.Combine(path, "retrieval-corpus-v1.json")), JsonOptions)
            ?? throw new InvalidDataException("Missing retrieval corpus.");
        RetrievalJudgmentSet judgments = JsonSerializer.Deserialize<RetrievalJudgmentSet>(
            File.ReadAllText(Path.Combine(path, "retrieval-judgments-v2.json")), JsonOptions)
            ?? throw new InvalidDataException("Missing retrieval judgments.");
        if (corpus.SchemaVersion != 1 || judgments.SchemaVersion != 2 ||
            judgments.Corpus != "retrieval-corpus-v1.json" ||
            corpus.Documents.Length != corpus.Documents.Select(document => document.Id).Distinct().Count() ||
            judgments.Judgments.Length != judgments.Judgments.Select(query => query.Name).Distinct().Count())
        {
            throw new InvalidDataException("Retrieval fixture identity or schema is invalid.");
        }

        HashSet<string> known = corpus.Documents.Select(document => document.Id)
            .ToHashSet(StringComparer.Ordinal);
        foreach (RetrievalJudgment judgment in judgments.Judgments)
        {
            if (string.IsNullOrWhiteSpace(judgment.Name) || string.IsNullOrWhiteSpace(judgment.Domain) ||
                string.IsNullOrWhiteSpace(judgment.Query) ||
                judgment.Relevant.Concat(judgment.LexicalExpected).Any(id => !known.Contains(id)) ||
                judgment.Relevant.Length != judgment.Relevant.Distinct().Count() ||
                judgment.LexicalExpected.Length != judgment.LexicalExpected.Distinct().Count())
            {
                throw new InvalidDataException("Retrieval judgments contain invalid source identities.");
            }
        }

        string workspace = Path.Combine(TestPaths.Temp, "praxis-benchmark-" + Guid.NewGuid().ToString("N"));
        string catalogPath = Path.Combine(workspace, "catalog");
        Directory.CreateDirectory(catalogPath);
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(workspace, privateMode);
            File.SetUnixFileMode(catalogPath, privateMode);
        }

        LocalCatalog catalog = LocalCatalog.Open(catalogPath);
        try
        {
            Dictionary<string, CapturedOriginal> originals = new(StringComparer.Ordinal);
            foreach (RetrievalBenchmarkDocument document in corpus.Documents)
            {
                using MemoryStream bytes = new(Encoding.UTF8.GetBytes(document.Content));
                originals.Add(document.Id, await catalog.Originals.CaptureFileAsync(
                    Path.Combine(workspace, "source", document.Path), bytes, bytes.Length, document.Id));
            }

            ProcessingDriver processor = ProcessingDriver.Text(catalog);
            for (int index = 0; index < corpus.Documents.Length; index++)
            {
                Assert.True(await processor.ProcessNextAsync());
            }

            Assert.False(await processor.ProcessNextAsync());
            await ProcessSegmentsAsync(catalog);
            foreach (RetrievalBenchmarkDocument document in corpus.Documents)
            {
                Assert.Equal(document.Searchable ? "completed" : "unsupported",
                    catalog.Text.Read(originals[document.Id].ItemId, 1)?.Outcome);
            }

            return new RetrievalBenchmarkFixture(catalog, judgments, originals, workspace);
        }
        catch
        {
            catalog.Dispose();
            Directory.Delete(workspace, recursive: true);
            throw;
        }
    }

    public static async Task ProcessSegmentsAsync(LocalCatalog catalog)
    {
        LocalTextSegmenter segmenter = new(catalog);
        for (int index = 0; index < 128; index++)
        {
            ProcessingLease? lease = catalog.Processing.TryClaim(segmenter.LeaseDuration,
                processorKey: LocalProcessors.TextSegments);
            if (lease is null)
            {
                return;
            }

            await segmenter.ProcessAsync(lease, CancellationToken.None);
            Assert.Equal("completed", Assert.Single(catalog.Processing.List(500),
                job => job.ItemId == lease.ItemId && job.Revision == lease.Revision &&
                    job.ProcessorKey == LocalProcessors.TextSegments).State);
        }

        throw new InvalidDataException("Segmentation exceeded the corpus job limit.");
    }

    public Guid[] Expected(IEnumerable<string> keys) =>
        keys.Select(key => Originals[key].ItemId).OrderBy(id => id).ToArray();

    public void VerifyHits(IReadOnlyList<LocalSearchHit> hits, bool lexical)
    {
        Assert.Equal(hits.Count, hits.Select(hit => hit.ItemId).Distinct().Count());
        foreach (LocalSearchHit hit in hits)
        {
            CapturedOriginal original = Assert.Single(Originals.Values,
                entry => entry.ItemId == hit.ItemId);
            Assert.Equal(original.SourceId, hit.SourceId);
            Assert.Equal(original.Revision, hit.Revision);
            Assert.Equal(original.Sha256, hit.OriginalSha256);
            Assert.EndsWith(hit.Title, hit.SourceLocator, StringComparison.Ordinal);
            Assert.InRange(hit.Snippet.Length, 1, 480);
            if (lexical)
            {
                Assert.Contains("⟦", hit.Snippet, StringComparison.Ordinal);
            }
            else
            {
                Assert.Contains(hit.Snippet, Catalog.Text.Read(hit.ItemId, hit.Revision)!.Content!,
                    StringComparison.Ordinal);
            }
        }
    }

    public long DatabaseBytes()
    {
        long total = new FileInfo(Catalog.DatabasePath).Length;
        string wal = Catalog.DatabasePath + "-wal";
        return total + (File.Exists(wal) ? new FileInfo(wal).Length : 0);
    }

    public void Dispose()
    {
        Catalog.Dispose();
        Directory.Delete(workspace, recursive: true);
    }
}
