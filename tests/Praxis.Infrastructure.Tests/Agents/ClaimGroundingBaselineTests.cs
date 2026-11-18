using System.Globalization;
using System.Text.Json;
using Praxis.Application.Agents.Runtime;
using Xunit;
using Xunit.Abstractions;

namespace Praxis.Infrastructure.Tests.Agents;

public sealed class ClaimGroundingBaselineTests(ITestOutputHelper output)
{
    [Fact]
    public void ReportsProvisionalClaimSupportConfusionWithoutPromotingAnUnreviewedGate()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "claim-grounding-cases-v1.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement fixture = document.RootElement;
        Assert.Equal(1, fixture.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("provisional", fixture.GetProperty("reviewStatus").GetString());

        JsonElement[] cases = fixture.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(15, cases.Length);
        Assert.Equal(cases.Length, cases.Select(entry => entry.GetProperty("id").GetString()!)
            .Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(5, cases.Select(entry => entry.GetProperty("sourceGroup").GetString()!)
            .Distinct(StringComparer.Ordinal).Count());

        List<(string Domain, string Expected, bool Accepted)> observations = [];
        int acceptedSupported = 0, acceptedUnsupported = 0, acceptedContradicted = 0, rejectedSupported = 0;
        foreach (JsonElement entry in cases)
        {
            string id = entry.GetProperty("id").GetString()!;
            string expected = entry.GetProperty("expected").GetString()!;
            string[] evidence = entry.GetProperty("evidence").EnumerateArray()
                .Select(item => item.GetString()!).ToArray();
            string answer = entry.GetProperty("answer").GetString()!;
            string question = entry.GetProperty("question").GetString()!;
            Assert.Contains(expected, new[] { "supported", "unsupported", "contradicted" });
            Assert.NotEmpty(evidence);
            Assert.All(evidence, item => Assert.False(string.IsNullOrWhiteSpace(item)));
            Assert.Contains("[1]", answer, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("rationale").GetString()));

            Dictionary<int, string> passages = evidence.Select((text, index) => (text, index))
                .ToDictionary(item => item.index + 1, item => item.text);
            HashSet<int> opened = Enumerable.Range(1, evidence.Length).ToHashSet();
            bool accepted = AnswerVerifier.Check(answer, evidence, question, true, false,
                evidence.Length, opened, passages).Clean;
            observations.Add((entry.GetProperty("domain").GetString()!, expected, accepted));
            if (accepted)
            {
                if (expected == "supported") acceptedSupported++;
                else if (expected == "unsupported") acceptedUnsupported++;
                else acceptedContradicted++;
            }
            else if (expected == "supported")
            {
                rejectedSupported++;
            }

            output.WriteLine($"case={id} expected={expected} accepted={accepted}");
        }

        foreach (var group in observations.GroupBy(item => item.Domain).OrderBy(group => group.Key))
        {
            int trueSupport = group.Count(item => item.Expected == "supported" && item.Accepted);
            int falseSupport = group.Count(item => item.Expected != "supported" && item.Accepted);
            output.WriteLine($"domain={group.Key} supported_accepted={trueSupport} false_supported={falseSupport}");
        }

        int falseSupported = acceptedUnsupported + acceptedContradicted;
        int accepted = acceptedSupported + falseSupported;
        output.WriteLine($"claim_support_baseline=v1 reviewed=false cases={cases.Length} " +
            $"accepted_supported={acceptedSupported} accepted_unsupported={acceptedUnsupported} " +
            $"accepted_contradicted={acceptedContradicted} rejected_supported={rejectedSupported} " +
            $"citation_precision={(accepted == 0 ? 0d : (double)acceptedSupported / accepted).ToString("F3", CultureInfo.InvariantCulture)} " +
            "model_tokens=n/a provider_latency=n/a");
    }
}
