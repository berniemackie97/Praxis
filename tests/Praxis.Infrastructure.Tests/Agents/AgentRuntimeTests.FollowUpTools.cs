using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Runtime;
using Praxis.Application.Agents.Tools;
using Praxis.Application.Policy;
using Praxis.Domain.Agents;
using Praxis.Domain.Policy;
using Praxis.Infrastructure.Agents;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Agents;

public sealed partial class AgentRuntimeTests
{
    [Fact]
    public async Task SourceReadsUseSmallerFollowUpSchemasWithoutBlockingCitations()
    {
        using SqliteCatalog catalog = await SeededAsync();
        Guid sourceId = Assert.Single(catalog.Search.Query("lease", 10)).SourceId;
        ScriptedBackend backend = new(
            _ => Turn(Call("knowledge_search", """{"query":"lease","limit":1}""")),
            _ => Turn(Call("knowledge_open", JsonSerializer.Serialize(new { sourceId }))),
            _ => new AgentTurn("The lease says rent is paid by check [2].", [], 10, 5));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        List<string> events = [];

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Read the lease and summarize the payment method",
            (kind, _, _) => events.Add(kind), CancellationToken.None);

        Assert.Equal(AgentReply.Completed, reply.StopReason);
        Assert.True(reply.Check.Clean);
        Assert.Equal(3, backend.ToolSets.Count);
        Assert.Equal(2, reply.ToolCalls.Count);
        Assert.Contains("lease.md", reply.Citations.Select(citation => citation.SourceName));
        Assert.Contains(backend.ToolSets[0], tool => tool.Name == "knowledge_timeline");
        Assert.DoesNotContain(backend.ToolSets[0], tool => tool.Name == ToolExpandTool.ToolName);
        Assert.DoesNotContain(backend.ToolSets[1], tool => tool.Name == "knowledge_timeline");
        Assert.DoesNotContain(backend.ToolSets[2], tool => tool.Name == "settings_read");
        Assert.All(backend.ToolSets.Skip(1), tools =>
        {
            Assert.Contains(tools, tool => tool.Name == "knowledge_search");
            Assert.Contains(tools, tool => tool.Name == "knowledge_open");
            Assert.Contains(tools, tool => tool.Name == "data_query");
            Assert.Contains(tools, tool => tool.Name == "skill_open");
            Assert.Contains(tools, tool => tool.Name == ToolExpandTool.ToolName);
        });
        Assert.True(backend.ToolSets[1].Count < backend.ToolSets[0].Count);
        Assert.True(backend.ToolSets[1].Sum(tool => tool.Description.Length + tool.InputSchema.GetRawText().Length) <
            backend.ToolSets[0].Sum(tool => tool.Description.Length + tool.InputSchema.GetRawText().Length));
        Assert.Equal(2, events.Count(kind => kind == "tool_finished"));

        const string completion = """{"choices":[{"message":{"content":"Done."}}],"usage":{"prompt_tokens":10,"completion_tokens":2}}""";
        using HttpClient fullHttp = new(new CapturingHandler(completion));
        using HttpClient narrowHttp = new(new CapturingHandler(completion));
        Uri endpoint = new("https://api.example.test/v1/");
        AgentMessage[] sameTranscript = [AgentMessage.FromUser("Summarize the lease")];
        AgentTurn fullWire = await new OpenAiCompatibleAgentBackend(fullHttp, endpoint, "test-model", null)
            .CompleteAsync("System", sameTranscript, backend.ToolSets[0], CancellationToken.None);
        AgentTurn narrowWire = await new OpenAiCompatibleAgentBackend(narrowHttp, endpoint, "test-model", null)
            .CompleteAsync("System", sameTranscript, backend.ToolSets[1], CancellationToken.None);
        Assert.NotNull(fullWire.Request);
        Assert.NotNull(narrowWire.Request);
        Assert.Equal(fullWire.Request.MessageBytes, narrowWire.Request.MessageBytes);
        Assert.Equal(fullWire.Request.SystemTextBytes, narrowWire.Request.SystemTextBytes);
        Assert.True(narrowWire.Request.ToolSchemaBytes < fullWire.Request.ToolSchemaBytes);
        Assert.True(narrowWire.Request.RequestBytes < fullWire.Request.RequestBytes);
    }

    [Fact]
    public async Task ExplicitToolExpansionRestoresPermittedCatalogAndKeepsNewlyUsedTool()
    {
        using SqliteCatalog catalog = await SeededAsync();
        new PolicyService(catalog.Policy).Request(new PolicyChange(ToolPermission: ToolPermissions.ReadOnly), PolicyActors.Owner);
        ScriptedBackend backend = new(
            _ => Turn(Call("knowledge_search", """{"query":"lease","limit":1}""")),
            _ => Turn(Call(ToolExpandTool.ToolName, "{}")),
            _ => Turn(Call("settings_read", "{}")),
            _ => new AgentTurn("The lease file was found [1].", [], 10, 5));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Find my lease and check privacy settings",
            (_, _, _) => { }, CancellationToken.None);

        Assert.Equal(AgentReply.Completed, reply.StopReason);
        Assert.True(reply.Check.Clean);
        Assert.Equal(4, backend.ToolSets.Count);
        Assert.Contains(backend.ToolSets[0], tool => tool.Name == "settings_read");
        Assert.DoesNotContain(backend.ToolSets[1], tool => tool.Name == "settings_read");
        Assert.Equal(backend.ToolSets[0].Select(tool => tool.Name).ToArray(), backend.ToolSets[2].Select(tool => tool.Name).ToArray());
        Assert.Contains(backend.ToolSets[3], tool => tool.Name == "settings_read");
        Assert.DoesNotContain(backend.ToolSets.SelectMany(tools => tools), tool => tool.Tier is ToolTiers.Propose or ToolTiers.Write or ToolTiers.System);
        Assert.Equal("ok", Assert.Single(catalog.Agents.ReadToolCalls(conversation.ConversationId, 20),
            record => record.Tool == ToolExpandTool.ToolName).Outcome);
    }

    [Fact]
    public async Task RejectedExpansionDoesNotReAdvertiseUnneededSchemas()
    {
        using SqliteCatalog catalog = await SeededAsync();
        ScriptedBackend backend = new(
            _ => Turn(Call("knowledge_search", """{"query":"lease","limit":1}""")),
            _ => Turn(Call(ToolExpandTool.ToolName, "[broken")),
            _ => new AgentTurn("The lease file was found [1].", [], 10, 5));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Find the lease",
            (_, _, _) => { }, CancellationToken.None);

        Assert.Equal(AgentReply.Completed, reply.StopReason);
        Assert.DoesNotContain(backend.ToolSets[2], tool => tool.Name == "settings_read");
        Assert.Equal("invalid_arguments", Assert.Single(catalog.Agents.ReadToolCalls(conversation.ConversationId, 10),
            record => record.Tool == ToolExpandTool.ToolName).Outcome);
    }

    [Fact]
    public async Task MissingExpansionToolDoesNotSilentlyHideOtherCapabilities()
    {
        using SqliteCatalog catalog = await SeededAsync();
        AgentBackendProfile profile = Local();
        catalog.Agents.SaveBackend(profile);
        AgentConversation conversation = catalog.Agents.Create(profile.BackendId, "without expansion");
        ScriptedBackend backend = new(
            _ => Turn(Call("knowledge_search", """{"query":"lease","limit":1}""")),
            _ => new AgentTurn("The lease file was found [1].", [], 10, 5));
        IAgentTool[] tools = Tools(catalog).Where(tool => tool.Definition.Name != ToolExpandTool.ToolName).ToArray();
        AgentRuntime runtime = AgentComposition.Runtime(catalog, tools, new FixedFactory(backend, null), clock);

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Find my lease",
            (_, _, _) => { }, CancellationToken.None);

        Assert.Equal(AgentReply.Completed, reply.StopReason);
        Assert.Equal(2, backend.ToolSets.Count);
        Assert.Equal(backend.ToolSets[0].Select(tool => tool.Name).ToArray(), backend.ToolSets[1].Select(tool => tool.Name).ToArray());
        Assert.Contains(backend.ToolSets[1], tool => tool.Name == "settings_read");
        Assert.DoesNotContain("tools_expand", backend.SystemPrompts[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task SessionBackendKeepsItsGrantedCatalogWithoutToolExpansion()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        AgentBackendProfile cli = new("codex", AgentBackendKinds.Codex, null, "gpt-test", null, "/usr/bin/true", true);
        catalog.Agents.SaveBackend(cli);
        AgentConversation conversation = catalog.Agents.Create(cli.BackendId, "session tools");
        SessionBackend session = new(_ => Task.FromResult(new AgentTurn("Done.", [], 10, 2)),
            inspectRequest: (_, tools) =>
            {
                Assert.Contains(tools, tool => tool.Name == "knowledge_search");
                Assert.Contains(tools, tool => tool.Name == "settings_read");
                Assert.DoesNotContain(tools, tool => tool.Name == ToolExpandTool.ToolName);
            });
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(null, session), clock);

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Summarize the available library tools",
            (_, _, _) => { }, CancellationToken.None);

        Assert.Equal(AgentReply.Completed, reply.StopReason);
        Assert.Equal(10, reply.InputTokens);
    }
}
