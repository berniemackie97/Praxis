using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Praxis.Application.Agents.Runtime;
using Praxis.Application.Inference;
using Praxis.Domain.Models;
using Praxis.Infrastructure.Inference.Ollama;
using Xunit;
using Xunit.Abstractions;

namespace Praxis.Infrastructure.Tests.Agents;

public sealed class ClaimSupportModelEvaluationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("claim-grounding-cases-v1.json", 15)]
    [InlineData("claim-grounding-cases-v2.json", 14)]
    public async Task MeasureInstalledLocalOllamaClaimSupportAgainstNoJudgeBaseline(string fixtureName, int expectedCount)
    {
        string? modelName = Environment.GetEnvironmentVariable("PRAXIS_GROUNDING_OLLAMA_MODEL");
        if (string.IsNullOrWhiteSpace(modelName))
        {
            output.WriteLine("NOT EXECUTED: set PRAXIS_GROUNDING_OLLAMA_MODEL to opt in to local inference.");
            return;
        }

        string endpoint = Environment.GetEnvironmentVariable("PRAXIS_GROUNDING_OLLAMA_ENDPOINT")
            ?? "http://127.0.0.1:11434";
        ModelProfile profile = new(ModelProfile.LocalEnrichment, ModelProfile.OllamaProvider,
            endpoint, modelName, 8_192, true);
        using ModelConnection connection = new OllamaChatModelConnector().Connect(profile.Normalized());
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(12));
        ModelIdentity identity = await connection.Model.DescribeAsync(timeout.Token);
        string fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fixture));
        JsonElement[] cases = document.RootElement.GetProperty("cases").EnumerateArray().ToArray();
        Assert.Equal(expectedCount, cases.Length);
        Assert.Equal("provisional", document.RootElement.GetProperty("reviewStatus").GetString());

        int baselineFalseSupport = 0, candidateFalseSupport = 0, supportedAccepted = 0;
        int correct = 0, abstained = 0, invalid = 0, inputTokens = 0, outputTokens = 0;
        double providerMs = 0;
        List<double> latencies = [];
        foreach (JsonElement item in cases)
        {
            string id = item.GetProperty("id").GetString()!;
            string expected = item.GetProperty("expected").GetString()!;
            string question = item.GetProperty("question").GetString()!;
            string claim = item.GetProperty("answer").GetString()!;
            string evidence = Assert.Single(item.GetProperty("evidence").EnumerateArray()).GetString()!;

            bool baselineAccepted = AnswerVerifier.Check(claim, [evidence], question, true, false,
                1, new HashSet<int> { 1 }, new Dictionary<int, string> { [1] = evidence }).Clean;
            if (baselineAccepted && expected != "supported")
            {
                baselineFalseSupport++;
            }

            Stopwatch clock = Stopwatch.StartNew();
            ClaimSupportResult result = await ClaimSupportProbe.CheckAsync(connection.Model,
                question, claim, evidence, timeout.Token);
            clock.Stop();
            latencies.Add(clock.Elapsed.TotalMilliseconds);
            providerMs += result.Usage.Duration.TotalMilliseconds;
            inputTokens += result.Usage.InputTokens;
            outputTokens += result.Usage.OutputTokens;

            if (result.Verdict == expected)
            {
                correct++;
            }
            if (result.Verdict == "supported" && expected == "supported")
            {
                supportedAccepted++;
            }
            if (result.Verdict == "supported" && expected != "supported")
            {
                candidateFalseSupport++;
            }
            if (result.Verdict == "uncertain")
            {
                abstained++;
            }
            if (result.Verdict is null)
            {
                invalid++;
            }

            output.WriteLine($"case={id} expected={expected} predicted={result.Verdict ?? "invalid"} " +
                $"latency_ms={clock.Elapsed.TotalMilliseconds:F1} tokens_in={result.Usage.InputTokens} " +
                $"tokens_out={result.Usage.OutputTokens} reason={result.Reason ?? "invalid_response"}");
        }

        double[] sorted = latencies.Order().ToArray();
        double Percentile(int percentage) => sorted[(int)Math.Ceiling(sorted.Length * percentage / 100d) - 1];
        int predictedSupported = supportedAccepted + candidateFalseSupport;
        output.WriteLine($"claim_support_candidate=v1 reviewed=false fixture={fixtureName} model={identity.Model} digest={identity.Digest} " +
            $"cases={cases.Length} correct={correct} baseline_false_support={baselineFalseSupport} " +
            $"candidate_false_support={candidateFalseSupport} supported_accepted={supportedAccepted} " +
            $"abstained={abstained} invalid={invalid} " +
            $"citation_precision={(predictedSupported == 0 ? 0d : (double)supportedAccepted / predictedSupported).ToString("F3", CultureInfo.InvariantCulture)} " +
            $"input_tokens={inputTokens} output_tokens={outputTokens} provider_ms={providerMs:F1} " +
            $"wall_total_ms={latencies.Sum():F1} p50_ms={Percentile(50):F1} p95_ms={Percentile(95):F1}");
        output.WriteLine("Local opt-in diagnostic only; no owner corpus or remote provider. " +
            "Fixture labels are provisional. Results are not production acceptance, hosted CI, or a promotion gate.");
    }
}
