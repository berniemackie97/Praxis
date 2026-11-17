using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Runtime;
using Praxis.Domain.Agents;
using Praxis.Domain.Policy;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Persistence;
using Xunit;

namespace Praxis.Infrastructure.Tests.Agents;

public sealed partial class AgentRuntimeTests
{
    [Fact]
    public async Task ReadOnlyModelReceivesOnlyPermittedToolSchemasAndTurnUsage()
    {
        using SqliteCatalog catalog = await SeededAsync();
        AgentToolCall call = Call("knowledge_search", """{"query":"lease","limit":1}""");
        AgentRequestFootprint first = new(8_000, 4_000, 3_000, 900, 0, 0, 9);
        AgentRequestFootprint second = new(9_000, 5_000, 3_000, 900, 1_300, 30, 9);
        ScriptedBackend backend = new(
            _ => new AgentTurn("", [call], 120, 20, first),
            _ => new AgentTurn("Your lease is in lease.md [1].", [], 150, 25, second));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        List<string> reported = [];

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Find my lease",
            (kind, _, detail) => { if (kind == "usage_turn") { reported.Add(detail!); } }, CancellationToken.None);

        Assert.Equal(AgentReply.Completed, reply.StopReason);
        Assert.Equal(270, reply.InputTokens);
        Assert.Equal(45, reply.OutputTokens);
        AgentTurnUsage[] turns = Assert.IsAssignableFrom<IReadOnlyList<AgentTurnUsage>>(reply.TurnUsage).ToArray();
        Assert.Equal(2, turns.Length);
        Assert.Equal(120, turns[0].InputTokens);
        Assert.Equal(20, turns[0].OutputTokens);
        Assert.Equal(1, turns[0].RequestedToolCalls);
        Assert.Equal(first, turns[0].Request);
        Assert.Equal(second, turns[1].Request);
        Assert.Equal(0, turns[1].RequestedToolCalls);
        Assert.Equal(2, reported.Count);
        using JsonDocument receipt = JsonDocument.Parse(reported[1]);
        Assert.Equal(270, receipt.RootElement.GetProperty("cumulativeInputTokens").GetInt32());
        Assert.Equal(9_000, receipt.RootElement.GetProperty("usage").GetProperty("request").GetProperty("requestBytes").GetInt32());
        Assert.DoesNotContain("lease.md", reported[1], StringComparison.Ordinal);

        Assert.Equal(2, backend.ToolSets.Count);
        foreach (IReadOnlyList<AgentToolDefinition> tools in backend.ToolSets)
        {
            Assert.NotEmpty(tools);
            Assert.All(tools, tool => Assert.Equal(ToolTiers.Read, tool.Tier));
            Assert.Contains(tools, tool => tool.Name == "knowledge_search");
            Assert.Contains(tools, tool => tool.Name == "knowledge_open");
            Assert.DoesNotContain(tools, tool => tool.Name == "artifact_create" || tool.Name == "memory_remember");
        }
    }

    [Fact]
    public async Task SessionUsageDoesNotInventHttpRequestSizes()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        AgentBackendProfile cli = new("codex", AgentBackendKinds.Codex, null, "gpt-test", null, "/usr/bin/true", true);
        catalog.Agents.SaveBackend(cli);
        AgentConversation chat = catalog.Agents.Create(cli.BackendId, "metrics");
        SessionBackend session = new(_ => Task.FromResult(new AgentTurn("Done.", [], 75, 11)));
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(null, session), clock);

        AgentReply reply = await runtime.RunAsync(chat.ConversationId, "Summarize the request",
            (_, _, _) => { }, CancellationToken.None);

        AgentTurnUsage turn = Assert.Single(reply.TurnUsage!);
        Assert.Equal(75, turn.InputTokens);
        Assert.Equal(11, turn.OutputTokens);
        Assert.Null(turn.Request);
        Assert.Equal(75, reply.InputTokens);
        Assert.Equal(11, reply.OutputTokens);
    }
}
