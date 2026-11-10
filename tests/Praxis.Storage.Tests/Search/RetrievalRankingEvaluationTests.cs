using Praxis.Storage.Search;
using Xunit;
using Xunit.Abstractions;

namespace Praxis.Storage.Tests.Search;

public sealed class RetrievalRankingEvaluationTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LargerCorpusPreservesLexicalExpectedSourcesAndProvenance()
    {
        using RetrievalBenchmarkFixture fixture = await RetrievalBenchmarkFixture.CreateAsync(
            "retrieval-corpus-v3.json", "retrieval-judgments-v3.json");
        Assert.Equal(31, fixture.Originals.Count);
        Assert.Equal(35, fixture.Judgments.Judgments.Length);
        RetrievalRankingReport report = new(output);

        foreach (RetrievalJudgment judgment in fixture.Judgments.Judgments)
        {
            Guid[] expected = fixture.Expected(judgment.LexicalExpected);
            LocalSearchHit[] hits = fixture.Catalog.Search.Query(judgment.Query, 5).ToArray();
            Assert.Equal(expected, hits.Select(hit => hit.ItemId).OrderBy(id => id).ToArray());
            fixture.VerifyHits(hits, lexical: true);
            report.Record(judgment.Domain, "lexical",
                fixture.Expected(judgment.Relevant), hits.Select(hit => hit.ItemId).ToArray());
            output.WriteLine($"query={judgment.Name} domain={judgment.Domain} " +
                $"lexical=[{string.Join(",", hits.Select(hit => fixture.SourceKey(hit.ItemId)))}]");
        }

        report.WriteSummary();
        output.WriteLine($"fixture=v{fixture.Judgments.SchemaVersion} " +
            $"sources={fixture.Originals.Count} queries={fixture.Judgments.Judgments.Length} " +
            $"catalog_bytes={fixture.DatabaseBytes()} runtime={fixture.RuntimeDescription}");
    }

    [Fact]
    public void RankingScorerSeparatesPrecisionRecallMrrAndAbstention()
    {
        Guid a = Guid.NewGuid();
        Guid b = Guid.NewGuid();
        Guid unrelated = Guid.NewGuid();
        RetrievalRankingScore ranked = RetrievalRankingScore.Calculate([a, b],
            [unrelated, b, a]);
        Assert.Equal(0, ranked.RecallAtOne);
        Assert.Equal(1, ranked.RecallAtThree);
        Assert.Equal(1, ranked.RecallAtFive);
        Assert.Equal(0, ranked.PrecisionAtOne);
        Assert.Equal(2d / 3, ranked.PrecisionAtThree);
        Assert.Equal(2d / 3, ranked.PrecisionAtFive);
        Assert.Equal(0.5, ranked.ReciprocalRank);
        Assert.False(ranked.NoAnswerCorrect);

        RetrievalRankingScore absent = RetrievalRankingScore.Calculate([], []);
        Assert.True(absent.NoAnswerCorrect);
        Assert.Equal(0, absent.ReciprocalRank);
        Assert.Equal(0, absent.RecallAtFive);
        Assert.False(RetrievalRankingScore.Calculate([], [unrelated]).NoAnswerCorrect);
        Assert.Throws<InvalidDataException>(() =>
            RetrievalRankingScore.Calculate([a], [a, a]));
    }
}
