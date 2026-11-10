using System.Buffers.Binary;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Praxis.Inference;
using Praxis.Storage.Originals;
using Praxis.Storage.Search;
using Xunit;
using Xunit.Abstractions;

namespace Praxis.Storage.Tests.Search;

public sealed class HybridRetrievalEvaluationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task VersionedJudgmentsMeasureLexicalSemanticAndFusedRetrievalWithoutLosingSourceIdentity()
    {
        using RetrievalBenchmarkFixture fixture = await RetrievalBenchmarkFixture.CreateAsync();
        TopicEmbeddingModel model = new();
        Assert.True((await fixture.Catalog.Embeddings.RefreshAsync(model, rebuild: true)).Indexed > 0);
        Dictionary<(string Domain, string Mode), List<RetrievalScore>> scores = new();
        int expectedNoAnswers = 0;
        int semanticFalseAnswers = 0;

        foreach (RetrievalJudgment judgment in fixture.Judgments.Judgments)
        {
            Guid[] relevant = fixture.Expected(judgment.Relevant);
            Stopwatch watch = Stopwatch.StartNew();
            LocalSearchHit[] lexical = fixture.Catalog.Search.Query(judgment.Query, 5).ToArray();
            var semantic = await fixture.Catalog.Embeddings.QueryAsync(judgment.Query, model, 5);
            HybridSearchResults fused = await fixture.Catalog.Hybrid.QueryAsync(judgment.Query, model, 5);
            watch.Stop();

            Assert.Equal(fixture.Expected(judgment.LexicalExpected),
                lexical.Select(hit => hit.ItemId).OrderBy(id => id).ToArray());
            Assert.True(fused.UsedEmbeddings);
            fixture.VerifyHits(lexical, lexical: true);
            fixture.VerifyHits(semantic.Select(hit => hit.Hit).ToArray(), lexical: false);
            foreach (LocalSearchHit hit in fused.Hits)
            {
                bool fromLexical = lexical.Any(item => item.ItemId == hit.ItemId);
                fixture.VerifyHits([hit], lexical: fromLexical);
            }

            Record("lexical", lexical.Select(hit => hit.ItemId).ToArray());
            Record("semantic", semantic.Select(hit => hit.Hit.ItemId).ToArray());
            Record("hybrid", fused.Hits.Select(hit => hit.ItemId).ToArray());

            if (judgment.Name is "mesh-concept" or "slope-concept" or
                "video-concept" or "orbit-concept")
            {
                Assert.Empty(lexical);
                Assert.Equal(Assert.Single(relevant), semantic[0].Hit.ItemId);
                Assert.Equal(semantic[0].Hit.ItemId, fused.Hits[0].ItemId);
            }

            if (relevant.Length == 0)
            {
                expectedNoAnswers++;
                if (semantic.Count > 0)
                {
                    semanticFalseAnswers++;
                }
            }

            output.WriteLine($"query={judgment.Name} domain={judgment.Domain} " +
                $"lexical={lexical.Length} semantic={semantic.Count} fused={fused.Hits.Count} " +
                $"elapsed_ms={watch.Elapsed.TotalMilliseconds:F2}");

            void Record(string mode, Guid[] ids)
            {
                var key = (judgment.Domain, mode);
                if (!scores.TryGetValue(key, out List<RetrievalScore>? list))
                {
                    list = new List<RetrievalScore>();
                    scores.Add(key, list);
                }

                list.Add(RetrievalScore.AtFive(relevant, ids));
            }
        }

        foreach (var pair in scores.OrderBy(pair => pair.Key.Domain)
            .ThenBy(pair => pair.Key.Mode))
        {
            output.WriteLine($"domain={pair.Key.Domain} mode={pair.Key.Mode} " +
                $"queries={pair.Value.Count} recall@5={pair.Value.Average(x => x.Recall):F3} " +
                $"precision@5={pair.Value.Average(x => x.Precision):F3} " +
                $"no_answer_correct={pair.Value.Count(x => x.NoAnswerCorrect)}");
        }

        Assert.True(expectedNoAnswers > 0);
        // A missing-answer query must remain in the scorecard even when a later ranking
        // change improves abstention; observed false positives are not expected outcomes.
        output.WriteLine($"expected_no_answer={expectedNoAnswers} " +
            $"semantic_false_answers={semanticFalseAnswers} " +
            $"fixture=v{fixture.Judgments.SchemaVersion} database_bytes={fixture.DatabaseBytes()} " +
            $"runtime={fixture.RuntimeDescription}");
    }

    [Fact]
    public async Task PartialRefreshRebuildBudgetAndRevisionsPreserveCanonicalEligibility()
    {
        using RetrievalBenchmarkFixture fixture = await RetrievalBenchmarkFixture.CreateAsync();
        TopicEmbeddingModel model = new();
        int expectedSegments = fixture.Originals.Values
            .Sum(original => fixture.Catalog.Segments.List(original.ItemId, original.Revision).Count);
        Assert.True(expectedSegments > 1);

        int indexed = 0;
        for (int iteration = 0; iteration < expectedSegments; iteration++)
        {
            SemanticIndexUpdate update = await fixture.Catalog.Embeddings.RefreshAsync(
                model, maxSegments: 1);
            indexed += update.Indexed;
            Assert.Equal(1, update.Indexed);
            Assert.Equal(iteration != expectedSegments - 1, update.HasMore);
        }

        Assert.Equal(expectedSegments, indexed);
        Assert.Equal(new SemanticIndexUpdate(0, false),
            await fixture.Catalog.Embeddings.RefreshAsync(model, maxSegments: 1));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Catalog.Embeddings.RefreshAsync(model, rebuild: true, maxSegments: 1));
        Assert.Equal(fixture.Originals["mesh"].ItemId,
            (await fixture.Catalog.Embeddings.QueryAsync("triangulation geometry", model))[0].Hit.ItemId);

        CapturedOriginal revoked = fixture.Originals["conflict"];
        Assert.True(fixture.Catalog.Sources.Revoke(revoked.SourceId));
        Assert.DoesNotContain((await fixture.Catalog.Embeddings.QueryAsync("orbital physics", model)),
            hit => hit.Hit.SourceId == revoked.SourceId);
        Assert.Empty(fixture.Catalog.Search.Query("contradiction"));

        CapturedOriginal original = fixture.Originals["mesh"];
        using MemoryStream revisedBytes = new(System.Text.Encoding.UTF8.GetBytes(
            "public sealed class MeshReader { public void UploadGeometry() { } }"));
        CapturedOriginal revised = await fixture.Catalog.Originals.CaptureFileAsync(
            Path.Combine(Path.GetDirectoryName(fixture.Catalog.DataDirectory)!, "source",
                "technical", "MeshReader.cs"), revisedBytes, revisedBytes.Length, "mesh-revision-v2");
        Assert.Equal(original.SourceId, revised.SourceId);
        Assert.Equal(original.ItemId, revised.ItemId);
        Assert.Equal(original.Revision + 1, revised.Revision);
        Assert.Empty(fixture.Catalog.Search.Query("ParseMesh"));
        Assert.DoesNotContain((await fixture.Catalog.Embeddings.QueryAsync("triangulation geometry", model)),
            hit => hit.Hit.ItemId == revised.ItemId);

        Assert.True(await Praxis.Storage.Tests.Support.ProcessingDriver.Text(fixture.Catalog)
            .ProcessNextAsync());
        await RetrievalBenchmarkFixture.ProcessSegmentsAsync(fixture.Catalog);
        Assert.Equal(1, (await fixture.Catalog.Embeddings.RefreshAsync(model)).Indexed);
        Assert.Empty(fixture.Catalog.Search.Query("ParseMesh"));
        Assert.Equal(revised.ItemId, Assert.Single(fixture.Catalog.Search.Query("UploadGeometry")).ItemId);

        int currentSegments = fixture.Originals.Values.Sum(captured =>
            fixture.Catalog.Segments.List(captured.ItemId, captured.Revision).Count) +
            fixture.Catalog.Segments.List(revised.ItemId, revised.Revision).Count;
        int refreshed = (await fixture.Catalog.Embeddings.RefreshAsync(model, rebuild: true)).Indexed;
        Assert.Equal(currentSegments, refreshed);
        Assert.DoesNotContain((await fixture.Catalog.Embeddings.QueryAsync("orbital physics", model)),
            hit => hit.Hit.SourceId == revoked.SourceId);
        await fixture.Catalog.Originals.VerifyAsync(revised.ItemId);
        await fixture.Catalog.Originals.VerifyAsync(revoked.ItemId);
    }

    [Fact]
    public async Task CorruptDerivedVectorsFailClosedAndMissingOriginalsFailVerification()
    {
        using RetrievalBenchmarkFixture fixture = await RetrievalBenchmarkFixture.CreateAsync();
        TopicEmbeddingModel model = new();
        Assert.True((await fixture.Catalog.Embeddings.RefreshAsync(model, rebuild: true)).Indexed > 0);
        CapturedOriginal mesh = fixture.Originals["mesh"];

        using (SqliteConnection db = new(new SqliteConnectionStringBuilder
        {
            DataSource = fixture.Catalog.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString()))
        {
            db.Open();
            using SqliteCommand read = db.CreateCommand();
            read.CommandText = """
                SELECT vector FROM semantic_vectors
                WHERE item_id = $item AND ordinal = 0
                """;
            read.Parameters.AddWithValue("$item", mesh.ItemId.ToString("D"));
            byte[] vector = Assert.IsType<byte[]>(read.ExecuteScalar());
            BinaryPrimitives.WriteSingleLittleEndian(vector.AsSpan(0, sizeof(float)), float.NaN);

            using SqliteCommand corrupt = db.CreateCommand();
            corrupt.CommandText = """
                UPDATE semantic_vectors SET vector = $vector
                WHERE item_id = $item AND ordinal = 0
                """;
            corrupt.Parameters.AddWithValue("$item", mesh.ItemId.ToString("D"));
            corrupt.Parameters.AddWithValue("$vector", vector);
            Assert.Equal(1, corrupt.ExecuteNonQuery());
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Catalog.Embeddings.QueryAsync("triangulation geometry", model));
        Assert.True((await fixture.Catalog.Embeddings.RefreshAsync(model, rebuild: true)).Indexed > 0);
        Assert.Equal(mesh.ItemId,
            (await fixture.Catalog.Embeddings.QueryAsync("triangulation geometry", model))[0].Hit.ItemId);

        string digest = mesh.Sha256;
        string originalPath = Path.Combine(fixture.Catalog.DataDirectory, "objects",
            digest[..2], digest[2..4], digest);
        File.Delete(originalPath);
        await Assert.ThrowsAnyAsync<IOException>(() =>
            fixture.Catalog.Originals.VerifyAsync(mesh.ItemId));
        // A digest is not proof the object still exists. Report cached hits without
        // requiring the current cache-availability policy to remain unchanged.
        IReadOnlyList<LocalSearchHit> cached = fixture.Catalog.Search.Query("ParseMesh");
        Assert.All(cached, hit => Assert.Equal(mesh.ItemId, hit.ItemId));
        output.WriteLine($"missing_original_cached_hits={cached.Count} verification=failed");
    }

    [Fact]
    public void ScorerPenalizesMissingEvidenceAndNonAnswerFalsePositives()
    {
        Guid correct = Guid.NewGuid();
        Guid irrelevant = Guid.NewGuid();
        Assert.Equal(new RetrievalScore(0.5, 0.5, false),
            RetrievalScore.AtFive([correct, Guid.NewGuid()], [correct, irrelevant]));
        Assert.Equal(new RetrievalScore(0, 0, false),
            RetrievalScore.AtFive([], [irrelevant]));
        Assert.Equal(new RetrievalScore(1, 1, true),
            RetrievalScore.AtFive([], []));
        Assert.Throws<InvalidDataException>(() =>
            RetrievalScore.AtFive([correct], [correct, correct]));
    }

    private sealed class TopicEmbeddingModel : IEmbeddingModel
    {
        private static readonly string[][] Concepts =
        [
            ["mesh", "parsemesh", "triangulation", "triangle", "geometry"],
            ["terrain", "atlas", "heightmap", "cliff", "slopes", "gradients"],
            ["vertex", "buffer", "drawing", "graphics", "tutorial", "gpu"],
            ["orbit", "orbital", "contradiction", "physics"],
            ["orion", "duplicate", "repeated"],
            ["ignore", "previous", "untrusted", "secrets"],
            ["finance", "salary", "invoice"],
        ];

        public Task<ModelIdentity> DescribeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ModelIdentity("fixture", "topic-v2", "topic-v2-stable", null, null, null));

        public Task<IReadOnlyList<float[]>> EmbedAsync(
            IReadOnlyList<string> inputs, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<float[]> vectors = inputs.Select(input =>
            {
                float[] vector = new float[Concepts.Length + 1];
                for (int index = 0; index < Concepts.Length; index++)
                {
                    if (Concepts[index].Any(word =>
                        input.Contains(word, StringComparison.OrdinalIgnoreCase)))
                    {
                        vector[index] = 1;
                    }
                }

                if (vector.All(value => value == 0))
                {
                    vector[^1] = 1;
                }

                return vector;
            }).ToArray();
            return Task.FromResult(vectors);
        }
    }
}
