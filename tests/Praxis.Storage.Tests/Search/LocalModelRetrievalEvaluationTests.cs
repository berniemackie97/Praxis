using System.Diagnostics;
using Praxis.Inference;
using Praxis.Inference.Ollama;
using Praxis.Storage.Search;
using Xunit;
using Xunit.Abstractions;

namespace Praxis.Storage.Tests.Search;

public sealed class LocalModelRetrievalEvaluationTests(ITestOutputHelper output)
{
    /// <summary>
    /// An opt-in owner-machine benchmark. Without an explicitly named local model it makes
    /// no model calls and prints NOT EXECUTED; it must not be cited as a green model benchmark.
    /// </summary>
    [Fact]
    public async Task MeasureInstalledLocalOllamaModelAgainstVersionedSourceJudgments()
    {
        string? modelName = Environment.GetEnvironmentVariable("PRAXIS_RETRIEVAL_OLLAMA_MODEL");
        if (string.IsNullOrWhiteSpace(modelName))
        {
            output.WriteLine("NOT EXECUTED: set PRAXIS_RETRIEVAL_OLLAMA_MODEL to a model already installed in local Ollama.");
            return;
        }

        string endpoint = Environment.GetEnvironmentVariable("PRAXIS_RETRIEVAL_OLLAMA_ENDPOINT")
            ?? "http://127.0.0.1:11434";
        using OllamaRuntime runtime = new(endpoint, TimeSpan.FromMinutes(3));
        IEmbeddingModel model = runtime.CreateEmbeddingModel(modelName);
        ModelIdentity identity = await model.DescribeAsync(CancellationToken.None);
        using RetrievalBenchmarkFixture fixture = await RetrievalBenchmarkFixture.CreateAsync();
        Stopwatch indexClock = Stopwatch.StartNew();
        SemanticIndexUpdate updated = await fixture.Catalog.Embeddings.RefreshAsync(model,
            rebuild: true, maxSegments: 512);
        indexClock.Stop();
        Assert.False(updated.HasMore);
        Assert.True(updated.Indexed > 0);

        Dictionary<(string Domain, string Mode), List<RetrievalScore>> scores = new();
        Stopwatch queryClock = Stopwatch.StartNew();
        foreach (RetrievalJudgment judgment in fixture.Judgments.Judgments)
        {
            Guid[] expected = fixture.Expected(judgment.Relevant);
            LocalSearchHit[] lexical = fixture.Catalog.Search.Query(judgment.Query, 5).ToArray();
            var semantic = await fixture.Catalog.Embeddings.QueryAsync(judgment.Query, model, 5);
            HybridSearchResults hybrid = await fixture.Catalog.Hybrid.QueryAsync(
                judgment.Query, model, 5);
            Assert.True(hybrid.UsedEmbeddings);
            Assert.Equal(fixture.Expected(judgment.LexicalExpected),
                lexical.Select(hit => hit.ItemId).OrderBy(id => id).ToArray());
            fixture.VerifyHits(lexical, lexical: true);
            fixture.VerifyHits(semantic.Select(hit => hit.Hit).ToArray(), lexical: false);
            foreach (LocalSearchHit hit in hybrid.Hits)
            {
                fixture.VerifyHits([hit],
                    lexical: lexical.Any(candidate => candidate.ItemId == hit.ItemId));
            }

            Record(judgment.Domain, "lexical", lexical.Select(hit => hit.ItemId).ToArray());
            Record(judgment.Domain, "semantic", semantic.Select(hit => hit.Hit.ItemId).ToArray());
            Record(judgment.Domain, "hybrid", hybrid.Hits.Select(hit => hit.ItemId).ToArray());
            output.WriteLine($"query={judgment.Name} relevant={expected.Length} " +
                $"lexical={lexical.Length} semantic={semantic.Count} hybrid={hybrid.Hits.Count}");

            void Record(string domain, string mode, Guid[] actual)
            {
                var key = (domain, mode);
                if (!scores.TryGetValue(key, out List<RetrievalScore>? domainScores))
                {
                    domainScores = new List<RetrievalScore>();
                    scores.Add(key, domainScores);
                }

                domainScores.Add(RetrievalScore.AtFive(expected, actual));
            }
        }

        queryClock.Stop();
        foreach (var group in scores.OrderBy(pair => pair.Key.Domain)
            .ThenBy(pair => pair.Key.Mode))
        {
            output.WriteLine($"domain={group.Key.Domain} mode={group.Key.Mode} " +
                $"queries={group.Value.Count} " +
                $"recall@5={group.Value.Average(score => score.Recall):F3} " +
                $"precision@5={group.Value.Average(score => score.Precision):F3} " +
                $"no_answer_correct={group.Value.Count(score => score.NoAnswerCorrect)}");
        }

        output.WriteLine($"model={modelName} digest={identity.Digest} " +
            $"fixture=v{fixture.Judgments.SchemaVersion} " +
            $"index_ms={indexClock.Elapsed.TotalMilliseconds:F2} " +
            $"query_ms={queryClock.Elapsed.TotalMilliseconds:F2} " +
            $"index_segments={updated.Indexed} catalog_bytes={fixture.DatabaseBytes()} " +
            $"test_process_working_set_bytes={Environment.WorkingSet} " +
            $"runtime={fixture.RuntimeDescription}");
        output.WriteLine("The working-set figure is for the test process, not Ollama; " +
            "report Ollama host memory separately. No model ranking threshold is assumed.");
    }
}
