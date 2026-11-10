using Xunit.Abstractions;

namespace Praxis.Storage.Tests.Search;

internal readonly record struct RetrievalRankingScore(
    int RelevantCount, int ReturnedCount,
    double RecallAtOne, double RecallAtThree, double RecallAtFive,
    double PrecisionAtOne, double PrecisionAtThree, double PrecisionAtFive,
    double ReciprocalRank, bool NoAnswerCorrect)
{
    public static RetrievalRankingScore Calculate(
        IReadOnlyCollection<Guid> relevant, IReadOnlyList<Guid> results)
    {
        Guid[] ranked = results.Take(5).ToArray();
        if (ranked.Length != ranked.Distinct().Count())
        {
            throw new InvalidDataException("A retrieval ranking repeated a canonical item.");
        }

        double Recall(int cutoff) => relevant.Count == 0 ? 0 :
            (double)ranked.Take(cutoff).Count(relevant.Contains) / relevant.Count;

        double Precision(int cutoff) =>
            (double)ranked.Take(cutoff).Count(relevant.Contains) / cutoff;

        int firstRelevant = Array.FindIndex(ranked, relevant.Contains);
        return new RetrievalRankingScore(
            relevant.Count, ranked.Length,
            Recall(1), Recall(3), Recall(5),
            Precision(1), Precision(3), Precision(5),
            firstRelevant < 0 ? 0 : 1d / (firstRelevant + 1),
            relevant.Count == 0 && ranked.Length == 0);
    }
}

internal sealed class RetrievalRankingReport(ITestOutputHelper output)
{
    private readonly Dictionary<(string Domain, string Mode), List<RetrievalRankingScore>> scores = new();

    public void Record(string domain, string mode,
        IReadOnlyCollection<Guid> expected, IReadOnlyList<Guid> actual)
    {
        var key = (domain, mode);
        if (!scores.TryGetValue(key, out List<RetrievalRankingScore>? values))
        {
            values = new List<RetrievalRankingScore>();
            scores.Add(key, values);
        }

        values.Add(RetrievalRankingScore.Calculate(expected, actual));
    }

    public void WriteSummary()
    {
        foreach (var group in scores.OrderBy(pair => pair.Key.Domain)
            .ThenBy(pair => pair.Key.Mode))
        {
            RetrievalRankingScore[] positive = group.Value
                .Where(score => score.RelevantCount > 0).ToArray();
            int noAnswerTotal = group.Value.Count - positive.Length;
            int noAnswerCorrect = group.Value.Count(score => score.NoAnswerCorrect);

            string quality = positive.Length == 0 ? "ranking=n/a" :
                $"recall@1={positive.Average(score => score.RecallAtOne):F3} " +
                $"recall@3={positive.Average(score => score.RecallAtThree):F3} " +
                $"recall@5={positive.Average(score => score.RecallAtFive):F3} " +
                $"precision@1={positive.Average(score => score.PrecisionAtOne):F3} " +
                $"precision@3={positive.Average(score => score.PrecisionAtThree):F3} " +
                $"precision@5={positive.Average(score => score.PrecisionAtFive):F3} " +
                $"mrr@5={positive.Average(score => score.ReciprocalRank):F3}";

            output.WriteLine($"domain={group.Key.Domain} mode={group.Key.Mode} " +
                $"queries={group.Value.Count} positive={positive.Length} {quality} " +
                $"no_answer_correct={noAnswerCorrect}/{noAnswerTotal}");
        }
    }
}
