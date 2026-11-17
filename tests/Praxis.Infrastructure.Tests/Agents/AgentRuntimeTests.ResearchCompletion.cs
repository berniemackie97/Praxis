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
