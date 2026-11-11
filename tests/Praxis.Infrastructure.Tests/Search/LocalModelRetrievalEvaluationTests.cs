using System.Diagnostics;
using Praxis.Application.Inference;
using Praxis.Application.Search;
using Praxis.Domain.Models;
using Praxis.Domain.Search;
using Praxis.Infrastructure.Inference.Ollama;
using Xunit;
using Xunit.Abstractions;

namespace Praxis.Infrastructure.Tests.Search;

public sealed class LocalModelRetrievalEvaluationTests(ITestOutputHelper output)
{
    /// <summary>
    /// Owner-authorized synthetic corpus measurement. Without an installed model selected
    /// through the environment, no inference runs and no real-model result is claimed.
    /// </summary>
    [Theory]
    [InlineData("retrieval-corpus-v1.json", "retrieval-judgments-v2.json")]
    [InlineData("retrieval-corpus-v3.json", "retrieval-judgments-v3.json")]
    public async Task MeasureInstalledLocalOllamaModelAgainstVersionedSourceJudgments(
        string corpusFile, string judgmentsFile)
    {
        string? modelName = Environment.GetEnvironmentVariable("PRAXIS_RETRIEVAL_OLLAMA_MODEL");
        if (string.IsNullOrWhiteSpace(modelName))
        {
            output.WriteLine("NOT EXECUTED: set PRAXIS_RETRIEVAL_OLLAMA_MODEL to an installed local embedding model.");
            return;
        }

        string endpoint = Environment.GetEnvironmentVariable("PRAXIS_RETRIEVAL_OLLAMA_ENDPOINT")
            ?? "http://127.0.0.1:11434";
        using OllamaRuntime runtime = new(endpoint, TimeSpan.FromMinutes(3));
        IEmbeddingModel model = runtime.CreateEmbeddingModel(modelName);
        ModelIdentity identity = await model.DescribeAsync(CancellationToken.None);
        using RetrievalBenchmarkFixture fixture = await RetrievalBenchmarkFixture.CreateAsync(
            corpusFile, judgmentsFile);

        Stopwatch indexClock = Stopwatch.StartNew();
        SemanticIndexUpdate updated = await new SemanticIndexer(fixture.Catalog.Embeddings).RefreshAsync(model,
            rebuild: true, maxSegments: 512);
        indexClock.Stop();
        Assert.False(updated.HasMore);
        int eligibleSegments = fixture.Originals.Values.Sum(original =>
            fixture.Catalog.Segments.List(original.ItemId, original.Revision).Count);
        Assert.True(eligibleSegments > 0);
        Assert.Equal(eligibleSegments, updated.Indexed);

        RetrievalRankingReport report = new(output);
        List<double> lexicalTimes = new();
        List<double> semanticTimes = new();
        List<double> hybridTimes = new();
        foreach (RetrievalJudgment judgment in fixture.Judgments.Judgments)
        {
            Guid[] relevant = fixture.Expected(judgment.Relevant);
            Stopwatch clock = Stopwatch.StartNew();
            SearchHit[] lexical = fixture.Catalog.Search.Query(judgment.Query, 5).ToArray();
            clock.Stop();
            lexicalTimes.Add(clock.Elapsed.TotalMilliseconds);

            clock.Restart();
            var semantic = await new SemanticRetriever(fixture.Catalog.Embeddings).QueryAsync(
                judgment.Query, model, 5);
            clock.Stop();
            semanticTimes.Add(clock.Elapsed.TotalMilliseconds);

            clock.Restart();
            HybridSearchResults hybrid = await new HybridSearch(fixture.Catalog.Search, new SemanticRetriever(fixture.Catalog.Embeddings)).QueryAsync(judgment.Query, model, 5);
            clock.Stop();
            hybridTimes.Add(clock.Elapsed.TotalMilliseconds);

            Assert.True(hybrid.UsedEmbeddings);
            Assert.Equal(fixture.Expected(judgment.LexicalExpected),
                lexical.Select(hit => hit.ItemId).OrderBy(id => id).ToArray());
            fixture.VerifyHits(lexical, lexical: true);
            fixture.VerifyHits(semantic.Select(hit => hit.Hit).ToArray(), lexical: false);
            foreach (SearchHit hit in hybrid.Hits)
            {
                fixture.VerifyHits([hit],
                    lexical: lexical.Any(candidate => candidate.ItemId == hit.ItemId));
            }

            report.Record(judgment.Domain, "lexical", relevant,
                lexical.Select(hit => hit.ItemId).ToArray());
            report.Record(judgment.Domain, "semantic", relevant,
                semantic.Select(hit => hit.Hit.ItemId).ToArray());
            report.Record(judgment.Domain, "hybrid", relevant,
                hybrid.Hits.Select(hit => hit.ItemId).ToArray());

            output.WriteLine($"query={judgment.Name} relevant=[{string.Join(",", judgment.Relevant)}] " +
                $"lexical=[{string.Join(",", lexical.Select(hit => fixture.SourceKey(hit.ItemId)))}] " +
                $"semantic=[{string.Join(",", semantic.Select(hit => fixture.SourceKey(hit.Hit.ItemId)))}] " +
                $"hybrid=[{string.Join(",", hybrid.Hits.Select(hit => fixture.SourceKey(hit.ItemId)))}] " +
                $"lexical_ms={lexicalTimes[^1]:F2} semantic_ms={semanticTimes[^1]:F2} " +
                $"hybrid_ms={hybridTimes[^1]:F2}");
        }

        report.WriteSummary();
        output.WriteLine($"model={modelName} digest={identity.Digest} " +
            $"fixture=v{fixture.Judgments.SchemaVersion} queries={fixture.Judgments.Judgments.Length} " +
            $"index_ms={indexClock.Elapsed.TotalMilliseconds:F2} " +
            $"index_segments={updated.Indexed} catalog_bytes={fixture.DatabaseBytes()} " +
            $"test_process_working_set_bytes={Environment.WorkingSet} " +
            $"runtime={fixture.RuntimeDescription}");
        WriteLatency("lexical", lexicalTimes);
        WriteLatency("semantic", semanticTimes);
        WriteLatency("hybrid", hybridTimes);
        output.WriteLine("Mode timings are sequential on one host, with separate model calls for " +
            "semantic and hybrid. They are not concurrent throughput or cold-start measures. " +
            "The test-process working set excludes Ollama process and GPU memory.");
    }

    private void WriteLatency(string mode, List<double> samples)
    {
        double[] ordered = samples.Order().ToArray();
        double P(int percent) => ordered[(int)Math.Ceiling(ordered.Length * percent / 100d) - 1];
        output.WriteLine($"mode={mode} calls={ordered.Length} " +
            $"total_ms={samples.Sum():F2} p50_ms={P(50):F2} p95_ms={P(95):F2}");
    }
}
