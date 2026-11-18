using System.Text.Json;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Agents;

public sealed class ClaimSupportProbeTests
{
    [Theory]
    [InlineData("supported")]
    [InlineData("unsupported")]
    [InlineData("contradicted")]
    [InlineData("uncertain")]
    public async Task StructuredVerdictsPreserveExactEvidenceAndOneBoundedRequest(string verdict)
    {
        ScriptedChatModel model = new(_ => JsonSerializer.Serialize(new { verdict, reason = "Source evidence differs." }));
        ClaimSupportResult result = await ClaimSupportProbe.CheckAsync(model,
            "What does the lease require?", "Checks are required [1].", "Paper checks are not accepted.",
            CancellationToken.None);

        Assert.Equal(verdict, result.Verdict);
        Assert.Equal(100, result.Usage.InputTokens);
        ChatRequest request = Assert.Single(model.Requests);
        Assert.Equal(2, request.Messages.Count);
        Assert.Equal(160, request.MaxOutputTokens);
        Assert.Equal(8_192, request.ContextTokens);
        Assert.Equal(0, request.Temperature);
        Assert.False(request.ResponseSchema.GetProperty("additionalProperties").GetBoolean());
        using JsonDocument prompt = JsonDocument.Parse(request.Messages[1].Content);
        Assert.Equal("Checks are required [1].", prompt.RootElement.GetProperty("claim").GetString());
        Assert.Equal("Paper checks are not accepted.", prompt.RootElement.GetProperty("excerpt").GetString());
        Assert.Contains("untrusted data", request.Messages[0].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedOrOutOfContractVerdictsDoNotCountAsSupported()
    {
        string[] outputs =
        [
            "not json",
            "{}",
            """{"verdict":"supported"}""",
            """{"verdict":"supported","reason":""}""",
            """{"verdict":"yes","reason":"No evidence."}""",
            """{"verdict":"supported","reason":"A reason.","unexpected":true}"""
        ];
        foreach (string output in outputs)
        {
            ScriptedChatModel model = new(_ => output);
            ClaimSupportResult result = await ClaimSupportProbe.CheckAsync(model,
                "What is true?", "Something is true.", "Something is not documented.",
                CancellationToken.None);
            Assert.Null(result.Verdict);
            Assert.Null(result.Reason);
            Assert.Single(model.Requests);
        }
    }

    [Fact]
    public async Task OversizedEvidenceNeverReachesTheModel()
    {
        ScriptedChatModel model = new(_ => throw new InvalidOperationException("No inference expected."));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            ClaimSupportProbe.CheckAsync(model, "Question", "Claim", new string('x', 5_001), CancellationToken.None));
        Assert.Empty(model.Requests);
    }
}
