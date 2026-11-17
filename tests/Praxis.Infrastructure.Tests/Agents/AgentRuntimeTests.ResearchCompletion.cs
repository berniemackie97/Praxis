using System.Text;
using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Runtime;
using Praxis.Domain.Agents;
using Praxis.Infrastructure.Agents;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Persistence;
using Xunit;

namespace Praxis.Infrastructure.Tests.Agents;

public sealed partial class AgentRuntimeTests
{
    [Fact]
    public async Task CompactFullHttpBudgetStopsBeforeSendingAndPreservesOwnerMessage()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        AgentBackendProfile profile = Local();
        catalog.Agents.SaveBackend(profile);
        AgentConversation compact = catalog.Agents.Create(profile.BackendId, "compact");
        catalog.Agents.SetContext(compact.ConversationId, AgentContextPreset.Compact);
        CapturingHandler handler = new("""{"choices":[{"message":{"content":"Accepted"}}],"usage":{"prompt_tokens":100,"completion_tokens":2}}""");
        using HttpClient http = new(handler);
        OpenAiCompatibleAgentBackend backend = new(http, new Uri(profile.Endpoint + "/"), profile.Model, null);
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(backend, null), clock);
        string owner = new('é', 25_000);
        List<string> events = [];

        AgentReply stopped = await runtime.RunAsync(compact.ConversationId, owner,
            (kind, _, _) => events.Add(kind), CancellationToken.None, useTools: false);

        Assert.Equal(AgentReply.ContextBudget, stopped.StopReason);
        Assert.Contains("context_limited", events);
        Assert.Null(handler.Body);
        Assert.Empty(stopped.TurnUsage!);
        AgentMessage[] saved = catalog.Agents.ReadMessages(compact.ConversationId).ToArray();
        Assert.Equal(owner, saved[0].Content);
        Assert.Equal(stopped.Text, saved[^1].Content);

        AgentConversation expanded = catalog.Agents.Create(profile.BackendId, "expanded");
        catalog.Agents.SetContext(expanded.ConversationId, AgentContextPreset.Balanced);
        AgentReply succeeded = await runtime.RunAsync(expanded.ConversationId, owner,
            (_, _, _) => { }, CancellationToken.None, useTools: false);

        Assert.Equal(AgentReply.Completed, succeeded.StopReason);
        Assert.NotNull(handler.Body);
        using JsonDocument payload = JsonDocument.Parse(handler.Body);
        Assert.Equal(owner, payload.RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        Assert.Equal(100, succeeded.InputTokens);
        Assert.Single(succeeded.TurnUsage!);
    }

    [Fact]
    public async Task BothHttpProvidersRejectOversizedJsonBeforeTransport()
    {
        const string openAiResponse = """{"choices":[{"message":{"content":"OK"}}],"usage":{"prompt_tokens":8,"completion_tokens":2}}""";
        const string anthropicResponse = """{"content":[{"type":"text","text":"OK"}],"usage":{"input_tokens":8,"output_tokens":2}}""";
        CapturingHandler openAiHandler = new(openAiResponse);
        CapturingHandler anthropicHandler = new(anthropicResponse);
        using HttpClient openAiHttp = new(openAiHandler);
        using HttpClient anthropicHttp = new(anthropicHandler);
        IAgentBudgetedBackend openAi = new OpenAiCompatibleAgentBackend(openAiHttp, new Uri("https://example.test/v1/"), "test-model", null);
        IAgentBudgetedBackend anthropic = new AnthropicAgentBackend(anthropicHttp, new Uri("https://example.test/"), "test-model", "test-key");
        AgentMessage[] transcript = [AgentMessage.FromUser("Sample " + new string('é', 2_000))];
        foreach (IAgentBudgetedBackend provider in new[] { openAi, anthropic })
        {
            AgentRequestTooLargeException error = await Assert.ThrowsAsync<AgentRequestTooLargeException>(() =>
                provider.CompleteWithinAsync("System", transcript, [], 128, CancellationToken.None));
            Assert.True(error.RequestBytes > error.MaximumRequestBytes);
            Assert.Equal(128, error.MaximumRequestBytes);
            AgentTurn accepted = await provider.CompleteWithinAsync("System", transcript, [], 100_000, CancellationToken.None);
            Assert.Equal("OK", accepted.Text);
            Assert.True(accepted.Request?.RequestBytes > 128);
        }

        Assert.NotNull(openAiHandler.Body);
        Assert.NotNull(anthropicHandler.Body);
    }

    [Fact]
    public async Task AutomaticRecallBytesAreReportedWithoutCopyingSourceTextIntoUsageEvents()
    {
        using SqliteCatalog catalog = await SeededAsync();
        AgentBackendProfile profile = Local() with { BackendId = "ollama" };
        catalog.Agents.SaveBackend(profile);
        AgentConversation chat = catalog.Agents.Create(profile.BackendId, "recall footprint");
        SessionBackend session = new(_ => Task.FromResult(new AgentTurn("The lease records payment by check [1].", [], 20, 4)));
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(null, session), clock);
        List<string> metrics = [];

        AgentReply reply = await runtime.RunAsync(chat.ConversationId, "How is rent paid under my lease?",
            (kind, _, detail) => { if (kind == "usage_turn") { metrics.Add(detail!); } },
            CancellationToken.None, useTools: false);

        AgentTurnUsage usage = Assert.Single(reply.TurnUsage!);
        Assert.True(usage.AutomaticRecallBytes > 0);
        Assert.Equal(0, usage.RecapBytes);
        Assert.Null(usage.Request);
        Assert.Equal(20, usage.InputTokens);
        string receipt = Assert.Single(metrics);
        Assert.Contains("automaticRecallBytes", receipt, StringComparison.Ordinal);
        Assert.DoesNotContain("Monthly rent is paid by check", receipt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedSixThousandCharacterSourcePagesStopWithPairedToolTraffic()
    {
        using SqliteCatalog catalog = await SeededAsync();
        using MemoryStream original = new(Encoding.UTF8.GetBytes("# Packet fixture\n" + new string('p', 210_000)));
        Guid sourceId = (await catalog.Originals.CaptureFileAsync(
            Path.Combine(inbox, "synthetic-packet.md"), original, original.Length, "synthetic-packet.md")).SourceId;
        int pages = 0;
        ScriptedBackend backend = new((_, _) => Task.FromResult(Turn(Call("knowledge_open",
            JsonSerializer.Serialize(new { sourceId, offset = pages++ * 6_000 })))));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        catalog.Agents.SetContext(conversation.ConversationId, AgentContextPreset.Expanded);
        List<string> events = [];

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Read the synthetic packet fixture",
            (kind, _, _) => events.Add(kind), CancellationToken.None,
            new AgentRunLimits(MaximumSteps: 16, MaximumInputTokens: 100_000));

        Assert.Equal(AgentReply.ContextBudget, reply.StopReason);
        Assert.Contains("context_limited", events);
        Assert.InRange(reply.SourceReads, 2, 15);
        Assert.Equal(reply.SourceReads, reply.ToolCalls.Count);
        AgentMessage[] transcript = catalog.Agents.ReadMessages(conversation.ConversationId).ToArray();
        Assert.Equal(reply.SourceReads, transcript.Count(message => message.Role == AgentRoles.Tool));
        Assert.Equal(reply.SourceReads, transcript.Count(message => message.Role == AgentRoles.Assistant && message.ToolCalls.Count > 0));
        Assert.Equal(reply.Text, transcript[^1].Content);
        Assert.DoesNotContain(transcript, message => message.Role == AgentRoles.Tool && message.Content.Length == 0);
    }

    [Fact]
    public async Task SearchCitationCannotSubstituteForAnOpenedPacketSource()
    {
        using SqliteCatalog catalog = await SeededAsync();
        using MemoryStream original = new(Encoding.UTF8.GetBytes("Synthetic walking packet example: opcode 0x2716, length 28 bytes."));
        Guid sourceId = (await catalog.Originals.CaptureFileAsync(
            Path.Combine(inbox, "synthetic-walk-packet.md"), original, original.Length, "synthetic-walk-packet.md")).SourceId;
        int searchCitation = 0;
        int openCitation = 0;
        ScriptedBackend backend = new(
            _ => Turn(Call("knowledge_search", """{"query":"lease","limit":1}""")),
            transcript =>
            {
                using JsonDocument search = JsonDocument.Parse(transcript.Last(message => message.Role == AgentRoles.Tool).Content);
                searchCitation = search.RootElement.GetProperty("citations")[0].GetProperty("n").GetInt32();
                return Turn(Call("knowledge_open", JsonSerializer.Serialize(new { sourceId })));
            },
            transcript =>
            {
                using JsonDocument source = JsonDocument.Parse(transcript.Last(message => message.Role == AgentRoles.Tool).Content);
                openCitation = source.RootElement.GetProperty("citations")[0].GetProperty("n").GetInt32();
                Assert.NotEqual(searchCitation, openCitation);
                return new AgentTurn($"The example uses opcode 0x2716 and 28 bytes [{searchCitation}].", [], 10, 4);
            },
            _ => new AgentTurn($"The example uses opcode 0x2716 and 28 bytes [{openCitation}].", [], 10, 4));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        List<string> events = [];

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId,
            "Read the synthetic walking packet and report its opcode and length",
            (kind, _, _) => events.Add(kind), CancellationToken.None);

        Assert.Equal(AgentReply.Completed, reply.StopReason);
        Assert.True(reply.Check.Clean);
        Assert.True(reply.Check.Repaired);
        Assert.Contains("repairing", events);
        Assert.Contains($"[{openCitation}]", reply.Text, StringComparison.Ordinal);
        Assert.Equal(1, reply.SearchCalls);
        Assert.Equal(1, reply.SourceReads);
        Assert.Equal(4, reply.TurnUsage!.Count);
    }

    [Fact]
    public async Task DisclosureRevokedBetweenToolAndModelTurnStopsWithoutForwardingOldEvidence()
    {
        using SqliteCatalog catalog = await SeededAsync();
        AgentBackendProfile remote = Local() with { BackendId = "remote", Endpoint = "https://example.test/v1" };
        Guid source = Assert.Single(catalog.Search.Query("lease", 10)).SourceId;
        catalog.Policy.ApplyChange(new Praxis.Domain.Policy.PolicyChange(Mode: Praxis.Domain.Policy.DisclosureModes.AllowAll),
            Praxis.Domain.Policy.PolicyActors.Owner);
        catalog.Policy.SaveCategory(new Praxis.Domain.Policy.SourceCategory(source, Praxis.Domain.Policy.DataCategories.Documents,
            Praxis.Domain.Policy.SourceCategory.OwnerOrigin, false));
        ScriptedBackend backend = new(_ => Turn(Call("knowledge_search", """{"query":"lease","limit":1}""")));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, remote, backend);

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Find my lease",
            (kind, tool, _) =>
            {
                if (kind == "tool_finished" && tool == "knowledge_search")
                {
                    catalog.Policy.SaveCategory(new Praxis.Domain.Policy.SourceCategory(source, Praxis.Domain.Policy.DataCategories.Documents,
                        Praxis.Domain.Policy.SourceCategory.OwnerOrigin, true));
                }
            }, CancellationToken.None);

        Assert.Equal(AgentReply.DisclosureChanged, reply.StopReason);
        Assert.Empty(reply.Citations);
        Assert.Single(backend.SystemPrompts);
        Assert.Equal(1, reply.SearchCalls);
        Assert.Equal(reply.Text, catalog.Agents.ReadMessages(conversation.ConversationId)[^1].Content);
        Assert.DoesNotContain("Monthly rent", reply.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepeatedUnsupportedPacketFigureHasExplicitEvidenceInsufficientStop()
    {
        using SqliteCatalog catalog = await SeededAsync();
        ScriptedBackend backend = new(
            _ => Turn(Call("knowledge_search", """{"query":"lease","limit":1}""")),
            _ => new AgentTurn("The packet opcode is 0xDEADBEEF [1].", [], 10, 4),
            _ => new AgentTurn("The packet opcode is 0xDEADBEEF [1].", [], 10, 4));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Find documented packet opcode",
            (_, _, _) => { }, CancellationToken.None);

        Assert.Equal(AgentReply.EvidenceInsufficient, reply.StopReason);
        Assert.Equal(3, reply.TurnUsage!.Count);
        Assert.Contains("couldn’t verify", reply.Text, StringComparison.Ordinal);
        Assert.Equal(reply.Text, catalog.Agents.ReadMessages(conversation.ConversationId)[^1].Content);
    }

    [Theory]
    [InlineData("The example uses opcode 0x2716 and 28 bytes [2].", false, false)]
    [InlineData("The example uses opcode 0xDEAD and 28 bytes [2].", true, false)]
    [InlineData("The example uses opcode 0x2716 and 28 bytes [1].", false, true)]
    [InlineData("The example uses opcode 0x2716 and 28 bytes [99].", false, true)]
    [InlineData("The example uses opcode 0x2716 and 28 bytes.", false, true)]
    public void TechnicalFiguresAndOpenedCitationsAreValidatedWithoutTreatingHexAsDecimal(string answer, bool unsupportedHex, bool citationFailure)
    {
        AnswerCheck result = AnswerVerifier.Check(answer,
            ["Synthetic fixture: opcode 0x2716, size 28 bytes."], "Report the packet format", true, false, 2, new HashSet<int> { 2 });

        Assert.Equal(unsupportedHex, result.UnsupportedFigures.Contains("0xDEAD", StringComparer.OrdinalIgnoreCase));
        Assert.Equal(citationFailure, result.MissingCitations);
    }
}
