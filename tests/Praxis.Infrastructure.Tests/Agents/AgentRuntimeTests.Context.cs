using System.Text;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Runtime;
using Praxis.Domain.Agents;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Persistence;
using Xunit;

namespace Praxis.Infrastructure.Tests.Agents;

public sealed partial class AgentRuntimeTests
{
    [Fact]
    public async Task OversizedActiveToolTrafficStopsBeforeAnotherModelRequest()
    {
        using SqliteCatalog catalog = await SeededAsync();
        await Capture(catalog, "large-evidence.md", Encoding.UTF8.GetBytes("# Evidence\n" + new string('a', 24_000)));
        Guid sourceId = Assert.Single(catalog.Library.List(20, 0, null)
            .Where(item => item.SourceLocator.EndsWith("large-evidence.md", StringComparison.Ordinal))).SourceId;
        int nextOffset = 0;
        ScriptedBackend backend = new((_, _) => Task.FromResult(Turn(Call("knowledge_open",
            $"""{"sourceId":"{{sourceId}}","offset":{{nextOffset++ * 6_000}}}"""))));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);

        List<string> events = [];
        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Read the evidence",
            (kind, _, _) => events.Add(kind), CancellationToken.None,
            new AgentRunLimits(MaximumSteps: 8, CompactAfterCharacters: 9_000));

        Assert.Equal(AgentReply.ContextBudget, reply.StopReason);
        Assert.Equal(2, backend.SystemPrompts.Count);
        Assert.Equal(2, reply.ToolCalls.Count);
        Assert.Contains("context_limited", events);
        Assert.Equal(1_800, reply.InputTokens);
        IReadOnlyList<AgentMessage> saved = catalog.Agents.ReadMessages(conversation.ConversationId);
        Assert.Equal(reply.Text, saved[^1].Content);
        Assert.Empty(saved[^1].Citations);
        Assert.Equal(2, saved.Count(message => message.Role == AgentRoles.Tool));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliHistoryIsBoundedByCompleteTurnsAndReportsOmissions(bool withSummary)
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        AgentBackendProfile cli = new("codex", AgentBackendKinds.Codex, null, "gpt-6", null, "/usr/bin/true", true);
        catalog.Agents.SaveBackend(cli);
        AgentConversation conversation = catalog.Agents.Create(cli.BackendId, "long chat");
        if (withSummary)
        {
            catalog.Agents.Append(conversation.ConversationId, new AgentMessage(AgentRoles.Summary, "Verified earlier summary.", [], null, []));
        }

        for (int index = 0; index < 8; index++)
        {
            catalog.Agents.Append(conversation.ConversationId, AgentMessage.FromUser($"turn-{index}: " + new string('q', 75)));
            catalog.Agents.Append(conversation.ConversationId, AgentMessage.FromAssistant("Answer: " + new string('a', 75), []));
        }

        AgentMessage[]? delivered = null;
        SessionBackend session = new(_ => Task.FromResult(new AgentTurn("Current reply", [], 0, 0)),
            messages => delivered = messages.ToArray());
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(null, session), clock);
        List<string> events = [];
        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "The current owner request.",
            (kind, _, _) => events.Add(kind), CancellationToken.None, new AgentRunLimits(CompactAfterCharacters: 520));

        Assert.NotNull(delivered);
        Assert.Equal("The current owner request.", delivered[^1].Content);
        Assert.True(reply.OmittedMessages > 0);
        Assert.Contains("context_limited", events);
        Assert.DoesNotContain(delivered, message => message.Content.StartsWith("turn-0:", StringComparison.Ordinal));
        Assert.Equal(withSummary, delivered[0].Role == AgentRoles.Summary);
        Assert.True(delivered.Sum(message => message.Content.Length == 0 ? 0 : message.Content.Length + 64) <= 520);
        Assert.Equal(reply.OmittedMessages, catalog.Agents.ReadMessages(conversation.ConversationId)
            .Count(message => message.Role != AgentRoles.Tool && message.Content.Length > 0) -
            delivered.Count(message => message.Role != AgentRoles.Tool && message.Content.Length > 0) - 1);
        Assert.Equal("Current reply", catalog.Agents.ReadMessages(conversation.ConversationId)[^1].Content);
    }

    [Fact]
    public async Task CliContextRetainsToolCallTurnAsOneUnitAndDoesNotTrimShortHistory()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        AgentBackendProfile cli = new("claude-code", AgentBackendKinds.ClaudeCode, null, "sonnet", null, "/usr/bin/true", true);
        catalog.Agents.SaveBackend(cli);
        AgentConversation conversation = catalog.Agents.Create(cli.BackendId, "tool chat");
        AgentToolCall call = new("call1", "knowledge_search", """{"query":"lease"}""");
        catalog.Agents.Append(conversation.ConversationId, AgentMessage.FromUser("Find my lease"));
        catalog.Agents.Append(conversation.ConversationId, AgentMessage.FromAssistant("", [call]));
        catalog.Agents.Append(conversation.ConversationId, AgentMessage.FromTool("call1", """{"matches":1}""", []));
        catalog.Agents.Append(conversation.ConversationId, AgentMessage.FromAssistant("Found one document.", []));

        AgentMessage[]? delivered = null;
        SessionBackend session = new(_ => Task.FromResult(new AgentTurn("Here it is.", [], 0, 0)),
            messages => delivered = messages.ToArray());
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(null, session), clock);
        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Open the document",
            (_, _, _) => { }, CancellationToken.None, new AgentRunLimits(CompactAfterCharacters: 480));

        Assert.NotNull(delivered);
        Assert.Equal([AgentRoles.User, AgentRoles.Assistant, AgentRoles.Tool, AgentRoles.Assistant, AgentRoles.User],
            delivered.Select(message => message.Role));
        Assert.Equal(0, reply.OmittedMessages);
    }
}
