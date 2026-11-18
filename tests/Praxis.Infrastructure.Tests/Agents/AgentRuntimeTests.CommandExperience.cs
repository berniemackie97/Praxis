using Praxis.Application.Agents;
using Praxis.Application.Agents.Runtime;
using Praxis.Domain.Agents;
using Praxis.Domain.Policy;
using Praxis.Infrastructure.Agents;
using Praxis.Infrastructure.Persistence;
using Xunit;

namespace Praxis.Infrastructure.Tests.Agents;

public sealed partial class AgentRuntimeTests
{
    [Fact]
    public async Task ReadOnlyDreamIsBlockedBeforeInferenceAndReportsWhy()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        catalog.Policy.ApplyChange(new PolicyChange(ToolPermission: ToolPermissions.ReadOnly), PolicyActors.Owner);
        ScriptedBackend backend = new(_ => throw new InvalidOperationException("No model call expected."));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "/dream", (_, _, _) => { }, CancellationToken.None);
        Assert.Equal("blocked", reply.Command!.Outcome);
        Assert.Contains("Propose edits", reply.Command.Summary, StringComparison.Ordinal);
        Assert.Empty(backend.SystemPrompts); Assert.Empty(reply.ToolCalls); Assert.Equal(0, reply.InputTokens);
    }

    [Fact]
    public async Task OwnerCorrectionPermissionDenialDoesNotCallAModelOrCreateAMemory()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        catalog.Policy.ApplyChange(new PolicyChange(ToolPermission: ToolPermissions.ReadOnly), PolicyActors.Owner);
        ScriptedBackend backend = new(_ => throw new InvalidOperationException("No model call expected."));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Project X is related to Project Y. Update your brain with that.", (_, _, _) => { }, CancellationToken.None);
        Assert.Equal("blocked", reply.Command!.Outcome); Assert.Empty(backend.SystemPrompts);
        Assert.Empty(catalog.Agents.ReadMemories(null, null, 10));
    }

    [Fact]
    public async Task CorrectCommandSavesTheExactStatementEvenWhenEntityMappingNeedsClarification()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        catalog.Policy.ApplyChange(new PolicyChange(ToolPermission: ToolPermissions.AllowEdits), PolicyActors.Owner);
        ScriptedBackend backend = new(_ => throw new InvalidOperationException("Unknown entities require clarification, not inference."));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "/correct Project X implements Product Y.", (_, _, _) => { }, CancellationToken.None);
        Assert.Equal("recorded_unmapped", reply.Command!.Outcome);
        Assert.Equal("Project X implements Product Y.", Assert.Single(catalog.Agents.ReadMemories(AgentMemory.Active, null, 10)).OwnerStatement);
        Assert.Empty(backend.SystemPrompts); Assert.Empty(catalog.Agents.ReadProposals(null, 10));
    }

    [Fact]
    public void CodexExposesKnowledgeProposalsButCannotExposeDirectWritesOrPrivateSkills()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        var allowed = CodexSessionBackend.ExposedTools(Tools(catalog).Select(tool => tool.Definition).ToArray());
        Assert.Contains(allowed, tool => tool.Name == "knowledge_propose");
        Assert.Contains(allowed, tool => tool.Name == "knowledge_maintenance");
        Assert.DoesNotContain(allowed, tool => tool.Tier == ToolTiers.Write || tool.LocalOnly || tool.Name == "agent_delegate");
    }

    [Theory]
    [InlineData("/DREAM  aliases", "dream", "aliases")]
    [InlineData("/dream\taliases", "dream", "aliases")]
    [InlineData("/name", "name", "")]
    public void CommandParserUsesOneGrammarForExecutionAndMetadata(string text, string name, string arguments)
    {
        var command = AgentCommands.Parse(text)!;
        Assert.Equal(name, command.Name); Assert.Equal(arguments, command.Arguments);
        Assert.Contains(AgentCommands.All, item => item.Name == name);
        Assert.Null(AgentCommands.Parse("/Users/owner/note.md"));
    }

    [Fact]
    public async Task DreamUsesACompactDedicatedPromptAndExcludesEarlierConversationBaggage()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        catalog.Policy.ApplyChange(new PolicyChange(ToolPermission: ToolPermissions.ProposeEdits), PolicyActors.Owner);
        ScriptedBackend backend = new(messages =>
        {
            Assert.DoesNotContain(messages, message => message.Content.Contains("Unrelated old discussion", StringComparison.Ordinal));
            return new AgentTurn("No supported change.", [], 1, 1);
        })
        { ExpectedTool = "knowledge_search" };
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        catalog.Agents.Append(conversation.ConversationId, AgentMessage.FromUser("Unrelated old discussion"));
        catalog.Agents.AppendFinal(conversation.ConversationId, AgentMessage.FromAssistant("Earlier response.", []));
        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "/dream aliases", (_, _, _) => { }, CancellationToken.None);
        Assert.True(Assert.Single(backend.SystemPrompts).Length < 3_000);
        Assert.Equal("incomplete", reply.Command!.Outcome); // A fluent answer alone is not a completed review.
        Assert.Equal(0, reply.Command.AppliedChanges);
    }

    [Fact]
    public async Task DurableCommandReceiptSurvivesCoordinatorAndCatalogReplacement()
    {
        Guid conversationId;
        using (SqliteCatalog catalog = SqliteCatalog.Open(root, clock))
        {
            catalog.Policy.ApplyChange(new PolicyChange(ToolPermission: ToolPermissions.ProposeEdits), PolicyActors.Owner);
            ScriptedBackend backend = new(_ => throw new InvalidOperationException("Empty Dream skips the model."));
            (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
            conversationId = conversation.ConversationId;
            AgentRunCoordinator runs = new(runtime, clock, catalog.AgentRuns);
            var reply = (await Poll(runs, runs.Start(conversationId, "/dream"))).Reply!;
            Assert.Equal("no_candidates", reply.Command!.Outcome);
        }
        using SqliteCatalog reopened = SqliteCatalog.Open(root, clock);
        var saved = Assert.Single(reopened.AgentRuns.CommandHistory(conversationId, 10));
        Assert.Equal("no_candidates", saved.Receipt.Outcome);
        Assert.True(saved.MessageSequence >= 2);
    }
}
