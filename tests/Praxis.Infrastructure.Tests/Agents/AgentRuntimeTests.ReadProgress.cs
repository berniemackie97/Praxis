using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Runtime;
using Praxis.Domain.Agents;
using Praxis.Domain.Policy;
using Praxis.Infrastructure.Persistence;
using Xunit;

namespace Praxis.Infrastructure.Tests.Agents;

public sealed partial class AgentRuntimeTests
{
    [Fact]
    public async Task RepeatedSearchIsAuditedWithoutReplayingItsEvidence()
    {
        using SqliteCatalog catalog = await SeededAsync();
        AgentToolCall original = Call("knowledge_search", """{"query":"lease","limit":1}""");
        AgentToolCall duplicate = Call("knowledge_search", """{"limit":1,"query":"lease"}""");
        ScriptedBackend backend = new(
            _ => Turn(original),
            _ => Turn(duplicate),
            _ => new AgentTurn("The lease note is in lease.md [1].", [], 10, 4));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Find my lease",
            (_, _, _) => { }, CancellationToken.None);

        Assert.Equal(AgentReply.Completed, reply.StopReason);
        Assert.True(reply.Check.Clean);
        Assert.NotEmpty(reply.Citations);
        Assert.Equal(2, reply.ToolCalls.Count);
        IReadOnlyList<AgentMessage> messages = catalog.Agents.ReadMessages(conversation.ConversationId);
        AgentMessage[] results = messages.Where(message => message.Role == AgentRoles.Tool).ToArray();
        Assert.Equal(2, results.Length);
        Assert.Contains("lease.md", results[0].Content, StringComparison.Ordinal);
        Assert.Contains("already_read", results[1].Content, StringComparison.Ordinal);
        Assert.DoesNotContain("lease.md", results[1].Content, StringComparison.Ordinal);
        using JsonDocument duplicateReceipt = JsonDocument.Parse(results[1].Content);
        Assert.Equal(original.CallId, duplicateReceipt.RootElement.GetProperty("previousCallId").GetString());
        AgentToolCallRecord[] audited = catalog.Agents.ReadToolCalls(conversation.ConversationId, 10).ToArray();
        Assert.Equal("ok", Assert.Single(audited, record => record.CallId == original.CallId).Outcome);
        Assert.Equal("duplicate_read", Assert.Single(audited, record => record.CallId == duplicate.CallId).Outcome);
    }

    [Fact]
    public async Task RepeatedReadsTriggerOneEvidenceOnlyAnswerTurn()
    {
        using SqliteCatalog catalog = await SeededAsync();
        ScriptedBackend backend = new(
            _ => Turn(Call("knowledge_search", """{"query":"lease","limit":1}""")),
            _ => Turn(Call("knowledge_search", """{"query":"lease","limit":1}""")),
            _ => Turn(Call("knowledge_search", """{"query":"lease","limit":1}""")),
            _ => new AgentTurn("The lease note is in lease.md [1].", [], 15, 4))
        {
            AllowToollessTurn = true
        };
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        List<string> events = [];

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Find the lease",
            (kind, _, _) => events.Add(kind), CancellationToken.None);

        Assert.Equal(AgentReply.Completed, reply.StopReason);
        Assert.True(reply.Check.Clean);
        Assert.Contains("progress_limited", events);
        Assert.Equal(4, backend.ToolCounts.Count);
        Assert.All(backend.ToolCounts.Take(3), count => Assert.True(count > 0));
        Assert.Equal(0, backend.ToolCounts[^1]);
        Assert.Equal(3, reply.ToolCalls.Count);
        Assert.Equal(2, catalog.Agents.ReadToolCalls(conversation.ConversationId, 10).Count(record => record.Outcome == "duplicate_read"));
        Assert.Equal(reply.Text, catalog.Agents.ReadMessages(conversation.ConversationId)[^1].Content);
    }

    [Fact]
    public async Task ToolCallInsteadOfEvidenceOnlyAnswerStopsWithPersistedReason()
    {
        using SqliteCatalog catalog = await SeededAsync();
        ScriptedBackend backend = new(
            _ => Turn(Call("knowledge_search", """{"query":"lease"}""")),
            _ => Turn(Call("knowledge_search", """{"query":"lease"}""")),
            _ => Turn(Call("knowledge_search", """{"query":"lease"}""")),
            _ => Turn(Call("knowledge_search", """{"query":"lease"}""")))
        {
            AllowToollessTurn = true
        };
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Find the lease",
            (_, _, _) => { }, CancellationToken.None);

        Assert.Equal(AgentReply.NoProgress, reply.StopReason);
        Assert.Equal(3, reply.ToolCalls.Count);
        Assert.Empty(catalog.Agents.ReadMessages(conversation.ConversationId)[^1].Citations);
        Assert.Equal(reply.Text, catalog.Agents.ReadMessages(conversation.ConversationId)[^1].Content);
        Assert.Equal(0, backend.ToolCounts[^1]);
    }

    [Fact]
    public async Task UnsupportedEvidenceOnlyAnswerCannotTriggerAnotherInferenceTurn()
    {
        using SqliteCatalog catalog = await SeededAsync();
        ScriptedBackend backend = new(
            _ => Turn(Call("knowledge_search", """{"query":"lease"}""")),
            _ => Turn(Call("knowledge_search", """{"query":"lease"}""")),
            _ => Turn(Call("knowledge_search", """{"query":"lease"}""")),
            _ => new AgentTurn("Monthly rent is $9,999.99 [1].", [], 10, 5))
        {
            AllowToollessTurn = true
        };
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Find the lease",
            (_, _, _) => { }, CancellationToken.None);

        Assert.Equal(AgentReply.NoProgress, reply.StopReason);
        Assert.Equal(4, backend.SystemPrompts.Count);
        Assert.Equal(0, backend.ToolCounts[^1]);
        Assert.DoesNotContain("9,999.99", reply.Text, StringComparison.Ordinal);
        Assert.Equal(reply.Text, catalog.Agents.ReadMessages(conversation.ConversationId)[^1].Content);
    }

    [Fact]
    public async Task NovelKnowledgeSearchResetsRepeatedReadProgress()
    {
        using SqliteCatalog catalog = await SeededAsync();
        ScriptedBackend backend = new(
            _ => Turn(Call("knowledge_search", """{"query":"lease"}""")),
            _ => Turn(Call("knowledge_search", """{"query":"lease"}""")),
            _ => Turn(Call("knowledge_search", """{"query":"rent"}""")),
            _ => Turn(Call("knowledge_search", """{"query":"lease"}""")),
            _ => new AgentTurn("The lease note covers rent [1].", [], 10, 5));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Find my lease and rent details",
            (_, _, _) => { }, CancellationToken.None);

        Assert.Equal(AgentReply.Completed, reply.StopReason);
        Assert.DoesNotContain(backend.SystemPrompts, prompt => prompt.Contains("Do not call tools again.", StringComparison.Ordinal));
        Assert.Equal(5, backend.SystemPrompts.Count);
        Assert.Equal(4, reply.ToolCalls.Count);
        AgentToolCallRecord[] audited = catalog.Agents.ReadToolCalls(conversation.ConversationId, 10).ToArray();
        Assert.Equal(2, audited.Count(record => record.Outcome == "duplicate_read"));
        Assert.Equal(2, audited.Count(record => record.Outcome == "ok"));
    }

    [Fact]
    public async Task SuccessfulReadMemoryIsIsolatedBetweenConversations()
    {
        using SqliteCatalog catalog = await SeededAsync();
        catalog.Agents.SaveBackend(Local());
        AgentConversation first = catalog.Agents.Create("local-llm", "first");
        AgentConversation second = catalog.Agents.Create("local-llm", "second");
        AgentToolRegistry registry = new(Tools(catalog), catalog.Agents, clock);
        AgentReadProgress firstReads = new();
        AgentReadProgress secondReads = new();
        AgentToolContext firstContext = new(first.ConversationId, "local-llm", true, ToolPermissions.ReadOnly, _ => true);
        AgentToolContext secondContext = firstContext with { ConversationId = second.ConversationId };
        string query = """{"query":"lease","limit":1}""";

        AgentToolOutput firstResult = await registry.InvokeAsync(Call("knowledge_search", query), firstContext, CancellationToken.None, firstReads);
        AgentToolOutput secondResult = await registry.InvokeAsync(Call("knowledge_search", query), secondContext, CancellationToken.None, secondReads);
        AgentToolOutput repeated = await registry.InvokeAsync(Call("knowledge_search", query), firstContext, CancellationToken.None, firstReads);

        Assert.NotEmpty(firstResult.Citations);
        Assert.NotEmpty(secondResult.Citations);
        Assert.Contains("already_read", repeated.Content, StringComparison.Ordinal);
        Assert.Equal(1, firstReads.ConsecutiveRepeats);
        Assert.Equal(0, secondReads.ConsecutiveRepeats);
        Assert.All(catalog.Agents.ReadToolCalls(second.ConversationId, 10),
            record => Assert.Equal("ok", record.Outcome));
    }

    [Fact]
    public async Task ChangedOffsetsAndPolicyRevocationDoNotReuseSourceReads()
    {
        using SqliteCatalog catalog = await SeededAsync();
        AgentToolRegistry registry = new(Tools(catalog), catalog.Agents, clock);
        AgentReadProgress reads = new();
        catalog.Agents.SaveBackend(Local());
        AgentConversation conversation = catalog.Agents.Create("local-llm", "read permission");
        Guid sourceId = Assert.Single(catalog.Library.List(20, 0, null)
            .Where(item => item.SourceLocator.EndsWith("lease.md", StringComparison.Ordinal))).SourceId;
        AgentToolContext permitted = new(conversation.ConversationId, "local-llm", true, ToolPermissions.ReadOnly, _ => true);
        string request = JsonSerializer.Serialize(new { sourceId, offset = 0 });
        AgentToolOutput first = await registry.InvokeAsync(Call("knowledge_open", request), permitted, CancellationToken.None, reads);
        Assert.NotEmpty(first.Citations);

        AgentToolOutput next = await registry.InvokeAsync(Call("knowledge_open", JsonSerializer.Serialize(new { sourceId, offset = 6_000 })),
            permitted, CancellationToken.None, reads);
        Assert.DoesNotContain("already_read", next.Content, StringComparison.Ordinal);

        AgentToolContext denied = permitted with { MayDisclose = _ => false };
        AgentToolOutput withheld = await registry.InvokeAsync(Call("knowledge_open", request), denied, CancellationToken.None, reads);
        Assert.NotEmpty(withheld.Withheld);
        Assert.Contains("withheld_by_disclosure_policy", withheld.Content, StringComparison.Ordinal);
        Assert.DoesNotContain(catalog.Agents.ReadToolCalls(permitted.ConversationId, 10),
            record => record.Outcome == "duplicate_read");
    }

    [Fact]
    public async Task InvalidReadArgumentsAreNotCachedAsSuccessfulEvidence()
    {
        using SqliteCatalog catalog = await SeededAsync();
        AgentToolRegistry registry = new(Tools(catalog), catalog.Agents, clock);
        AgentReadProgress reads = new();
        catalog.Agents.SaveBackend(Local());
        AgentConversation conversation = catalog.Agents.Create("local-llm", "invalid read");
        AgentToolContext context = new(conversation.ConversationId, "local-llm", true, ToolPermissions.ReadOnly, _ => true);
        AgentToolOutput first = await registry.InvokeAsync(Call("knowledge_open", """{"sourceId":"not-a-guid"}"""), context, CancellationToken.None, reads);
        AgentToolOutput second = await registry.InvokeAsync(Call("knowledge_open", """{"sourceId":"not-a-guid"}"""), context, CancellationToken.None, reads);

        Assert.Contains("invalid_arguments", first.Content, StringComparison.Ordinal);
        Assert.Equal(first.Content, second.Content);
        Assert.Equal(0, reads.ConsecutiveRepeats);
        Assert.All(catalog.Agents.ReadToolCalls(context.ConversationId, 10),
            record => Assert.Equal("invalid_arguments", record.Outcome));
    }
}
