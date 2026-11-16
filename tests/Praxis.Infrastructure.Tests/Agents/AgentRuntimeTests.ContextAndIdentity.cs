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
    public async Task ContextPresetChangesHistoricalScopeWithoutChangingTheProviderAndPersists()
    {
        Guid id;
        using (SqliteCatalog catalog = SqliteCatalog.Open(root, clock))
        {
            AgentBackendProfile profile = new("codex", AgentBackendKinds.Codex, null, "default", null, "/usr/bin/true", true);
            catalog.Agents.SaveBackend(profile);
            AgentConversation chat = catalog.Agents.Create(profile.BackendId, "context");
            id = chat.ConversationId;
            catalog.Agents.Append(id, AgentMessage.FromUser("Earlier turn " + new string('a', 13_000)));
            catalog.Agents.Append(id, AgentMessage.FromAssistant("Earlier answer.", []));
            IReadOnlyList<AgentMessage> delivered = [];
            SessionBackend session = new(_ => Task.FromResult(new AgentTurn("Received.", [], 0, 0)), messages => delivered = messages);
            AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(null, session), clock);

            Assert.Throws<ArgumentException>(() => catalog.Agents.SetContext(id, "unbounded"));
            Assert.Equal(AgentContextPreset.Compact, catalog.Agents.SetContext(id, AgentContextPreset.Compact).ContextPreset);
            AgentReply compact = await runtime.RunAsync(id, "Check current scope.", (_, _, _) => { }, CancellationToken.None);
            Assert.True(compact.OmittedMessages > 0);
            Assert.DoesNotContain(delivered, message => message.Content.StartsWith("Earlier turn", StringComparison.Ordinal));

            AgentConversation expanded = catalog.Agents.SetContext(id, AgentContextPreset.Expanded);
            Assert.Equal(profile.BackendId, expanded.BackendId);
            await runtime.RunAsync(id, "Check current scope again.", (_, _, _) => { }, CancellationToken.None);
            Assert.Contains(delivered, message => message.Content.StartsWith("Earlier turn", StringComparison.Ordinal));
        }

        using SqliteCatalog reopened = SqliteCatalog.Open(root, clock);
        Assert.Equal(AgentContextPreset.Expanded, reopened.Agents.Read(id)?.ContextPreset);
    }

    [Theory]
    [InlineData("Who are you?", false)]
    [InlineData("Who made you?", true)]
    [InlineData("Are you Google?", true)]
    [InlineData("Which model are you using?", false)]
    public async Task AssistantIdentityDoesNotDependOnTheProviderAnswer(string question, bool useTools)
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        AgentBackendProfile profile = new("google-model", AgentBackendKinds.OpenAiCompatible, "https://example.invalid/", "gemma-fixture", null, null, true);
        catalog.Agents.SaveBackend(profile);
        AgentConversation chat = catalog.Agents.Create(profile.BackendId, "identity");
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(null, null), clock);

        AgentReply reply = await runtime.RunAsync(chat.ConversationId, question, (_, _, _) => { }, CancellationToken.None, useTools: useTools);
        Assert.Contains("Praxis", reply.Text, StringComparison.Ordinal);
        Assert.Equal(question.StartsWith("Which model", StringComparison.Ordinal), reply.Text.Contains("gemma-fixture", StringComparison.Ordinal));
        Assert.Equal(0, reply.InputTokens);
        Assert.Equal(reply.Text, catalog.Agents.ReadMessages(chat.ConversationId)[^1].Content);
    }

    [Theory]
    [InlineData(ToolPermissions.AllowEdits, true, true)]
    [InlineData(ToolPermissions.ProposeEdits, true, false)]
    [InlineData(ToolPermissions.ReadOnly, true, false)]
    [InlineData(ToolPermissions.AllowEdits, false, false)]
    public async Task OwnerNameCorrectionUsesMemoryPermissionsAndNeedsNoModel(string permission, bool useTools, bool applied)
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        AgentBackendProfile profile = Local() with { BackendId = "ollama" };
        catalog.Agents.SaveBackend(profile);
        AgentConversation chat = catalog.Agents.Create(profile.BackendId, "names");
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(null, null), clock);
        Application.Policy.PolicyService policy = new(catalog.Policy);
        policy.Request(new PolicyChange(ToolPermission: permission), PolicyActors.Owner);
        AgentMemory alreadySaved = catalog.Agents.AddMemory(null, "My resale business name is Sequoia Toolworks LLC.", AgentMemory.Active);

        const string correction = """The "Harbor Resale Project" has a name, the company is called Sequoia Toolworks LLC save that to memory update that so you dont call it "Harbor Resale Project" any more lol""";
        AgentReply result = await runtime.RunAsync(chat.ConversationId, correction, (_, _, _) => { }, CancellationToken.None, useTools: useTools);
        await runtime.RunAsync(chat.ConversationId, correction, (_, _, _) => { }, CancellationToken.None, useTools: useTools);
        Assert.Empty(policy.Pending());
        Assert.Equal(0, result.InputTokens);
        Assert.Contains(catalog.Agents.ReadMemories(AgentMemory.Active, null, 10), item => item == alreadySaved);
        Assert.Equal(applied ? 1 : 0, catalog.Agents.ReadNameCorrections().Count);
        if (permission == ToolPermissions.ProposeEdits)
        {
            AgentMemory proposed = Assert.Single(catalog.Agents.ReadMemories(AgentMemory.Proposed, null, 10));
            Assert.Contains("Approvals", result.Text, StringComparison.Ordinal);
            Assert.Contains("Sequoia Toolworks LLC", proposed.Text, StringComparison.Ordinal);
            Assert.True(catalog.Agents.SetMemoryStatus(proposed.MemoryId, AgentMemory.Active));
        }
        else if (!applied)
        {
            Assert.Empty(catalog.Agents.ReadMemories(AgentMemory.Proposed, null, 10));
            Assert.Contains("haven’t saved", result.Text, StringComparison.Ordinal);
            return;
        }

        AgentConversation newChat = catalog.Agents.Create(profile.BackendId, "new conversation");
        AgentReply answer = await runtime.RunAsync(newChat.ConversationId, "what is my business name?", (_, _, _) => { }, CancellationToken.None, useTools: false);
        Assert.Equal("Your saved business name is **Sequoia Toolworks LLC**.", answer.Text);
        Assert.Single(catalog.Agents.ReadNameCorrections());
    }
}
