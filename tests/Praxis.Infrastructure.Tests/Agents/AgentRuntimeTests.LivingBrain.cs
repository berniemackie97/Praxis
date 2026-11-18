using Microsoft.Data.Sqlite;
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
    public async Task DreamSessionRelayEnforcesTheSameRestrictedToolScope()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        catalog.Policy.ApplyChange(new PolicyChange(ToolPermission: ToolPermissions.AllowEdits), PolicyActors.Owner);
        catalog.Agents.SaveBackend(Local());
        AgentConversation conversation = catalog.Agents.Create(Local().BackendId, "Session Dream");
        SessionBackend session = new(async invoke =>
        {
            string denied = await invoke(Call("artifact_create", """{"title":"Injected","markdown":"Write this"}"""), CancellationToken.None);
            Assert.Contains("operation_tool_denied", denied, StringComparison.Ordinal);
            return new AgentTurn("No changes were applied.", [], 1, 1);
        }, inspectRequest: (_, tools) => Assert.DoesNotContain(tools, tool => tool.Name == "artifact_create"));
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(null, session), clock);
        await runtime.RunAsync(conversation.ConversationId, "/dream inspect a topic", (_, _, _) => { }, CancellationToken.None);
        Assert.Empty(catalog.Agents.ReadArtifacts(10));
    }

    [Fact]
    public void RecapSearchUpgradeBackfillsExistingSummariesAndTracksLaterEdits()
    {
        Guid conversationId;
        using (SqliteCatalog catalog = SqliteCatalog.Open(root, clock))
        {
            catalog.Agents.SaveBackend(Local());
            conversationId = catalog.Agents.Create(Local().BackendId, "Orchid archive").ConversationId;
            catalog.Agents.Append(conversationId, AgentMessage.FromUser("Orchid ships Friday."));
            catalog.Agents.AppendFinal(conversationId, AgentMessage.FromAssistant("Acknowledged.", []));
            clock.UtcNow += TimeSpan.FromSeconds(10);
            Assert.True(catalog.ChatMemory.SaveRecap(catalog.ChatMemory.Next()!, "Orchid ships Friday."));
        }

        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(root, "catalog.sqlite"), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
        {
            connection.Open();
            using SqliteCommand previous = connection.CreateCommand();
            previous.CommandText = """
                DROP TABLE knowledge_vocabulary_extensions;
                DROP INDEX agent_runs_command_history;
                DROP TABLE agent_run_reviews;
                ALTER TABLE agent_memories DROP COLUMN owner_statement;
                DROP TRIGGER agent_run_forget_skills;
                DROP TABLE agent_skill_experiences;
                DROP TABLE agent_learning_attempts;
                DROP TABLE agent_run_events;
                DROP TABLE agent_runs;
                DROP TABLE processing_controls;
                ALTER TABLE agent_skill_revisions DROP COLUMN evidence_invalidated;
                ALTER TABLE link_settings DROP COLUMN study_media;
                ALTER TABLE link_settings DROP COLUMN sample_frames;
                ALTER TABLE link_settings DROP COLUMN vision_model;
                ALTER TABLE link_settings DROP COLUMN maximum_media_minutes;
                DROP TRIGGER agent_recap_search_insert;
                DROP TRIGGER agent_recap_search_update;
                DROP TRIGGER agent_recap_search_delete;
                DROP TABLE agent_recap_search;
                PRAGMA user_version = 38;
                """;
            previous.ExecuteNonQuery();
        }

        using SqliteCatalog upgraded = SqliteCatalog.Open(root, clock);
        Assert.Equal(conversationId, Assert.Single(upgraded.ChatMemory.Search("Orchid", null, 3)).ConversationId);
        upgraded.Agents.Append(conversationId, AgentMessage.FromUser("Orchid now ships Monday."));
        upgraded.Agents.AppendFinal(conversationId, AgentMessage.FromAssistant("Acknowledged.", []));
        clock.UtcNow += TimeSpan.FromSeconds(10);
        Assert.True(upgraded.ChatMemory.SaveRecap(upgraded.ChatMemory.Next()!, "Orchid now ships Monday."));
        Assert.Empty(upgraded.ChatMemory.Search("Friday", null, 3));
        Assert.Single(upgraded.ChatMemory.Search("Monday", null, 3));
    }

    [Fact]
    public async Task DreamIntentCannotBeDispatchedAsADirectSettingsChange()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        catalog.Policy.ApplyChange(new PolicyChange(ToolPermission: ToolPermissions.ProposeEdits), PolicyActors.Owner);
        ScriptedBackend backend = new(_ => new AgentTurn("No supported repair was found.", [], 1, 1)) { ExpectedTool = "knowledge_search" };
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        await runtime.RunAsync(conversation.ConversationId, "/dream set privacy to allow all", (_, _, _) => { }, CancellationToken.None);
        Assert.Empty(catalog.Policy.ReadPendingChanges());
        Assert.Contains("Dream knowledge-maintenance", Assert.Single(backend.SystemPrompts), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DreamStopsAnUnproductiveModelAtItsOwnStepBudget()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        catalog.Policy.ApplyChange(new PolicyChange(ToolPermission: ToolPermissions.ProposeEdits), PolicyActors.Owner);
        int expression = 0;
        ScriptedBackend backend = new((_, _) => Task.FromResult(Turn(Call("compute_evaluate", "{\"expression\":\"" + ++expression + "+1\"}"))))
        { ExpectedTool = "knowledge_search" };
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        AgentReply result = await runtime.RunAsync(conversation.ConversationId, "/dream inspect a topic", (_, _, _) => { }, CancellationToken.None,
            new AgentRunLimits(MaximumSteps: 64));
        Assert.Equal(AgentReply.StepLimit, result.StopReason);
        Assert.Equal(8, backend.SystemPrompts.Count);
        Assert.Empty(catalog.Agents.ReadProposals(null, 10));
    }

    [Fact]
    public async Task DreamCannotWriteOrEscalateEvenWhenOrdinaryChatAllowsEdits()
    {
        using SqliteCatalog catalog = await SeededAsync();
        catalog.Policy.ApplyChange(new PolicyChange(ToolPermission: ToolPermissions.AllowEdits), PolicyActors.Owner);
        ScriptedBackend backend = new(
            _ => Turn(Call("artifact_create", """{"title":"Injected","markdown":"Write this"}"""),
                Call("agent_delegate", "{}"), Call("memory_remember", """{"text":"unverified"}""")),
            _ => new AgentTurn("No changes were applied.", [], 1, 1))
        { ExpectedTool = "knowledge_search" };
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        AgentReply result = await runtime.RunAsync(conversation.ConversationId, "/dream inspect the lease", (_, _, _) => { }, CancellationToken.None);
        Assert.Equal(AgentReply.Completed, result.StopReason);
        Assert.Empty(catalog.Agents.ReadArtifacts(10));
        Assert.Empty(catalog.Agents.ReadMemories(null, null, 10));
        Assert.All(catalog.Agents.ReadToolCalls(conversation.ConversationId, 10), call => Assert.Equal("permission_denied", call.Outcome));
        Assert.All(backend.ToolSets, tools => Assert.DoesNotContain(tools, tool => tool.Name is "artifact_create" or "agent_delegate" or "settings_propose"));
        Assert.Contains("Dream knowledge-maintenance", backend.SystemPrompts[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyDreamUsesExistingQueueAndSkipsInferenceWhenNothingIsEligible()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        catalog.Policy.ApplyChange(new PolicyChange(ToolPermission: ToolPermissions.ProposeEdits), PolicyActors.Owner);
        ScriptedBackend backend = new(_ => throw new InvalidOperationException("No model call expected."));
        (AgentRuntime runtime, AgentConversation conversation) = Runtime(catalog, Local(), backend);
        AgentReply result = await runtime.RunAsync(conversation.ConversationId, "/dream", (_, _, _) => { }, CancellationToken.None);
        Assert.Equal("no_candidates", result.Command!.Outcome);
        Assert.Equal(0, result.InputTokens);
        Assert.Empty(backend.SystemPrompts);
        Assert.Single(catalog.Agents.ReadToolCalls(conversation.ConversationId, 10));
    }

    [Fact]
    public async Task DreamWithoutToolsDoesNotPretendToInspectKnowledge()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        catalog.Agents.SaveBackend(Local());
        AgentConversation conversation = catalog.Agents.Create(Local().BackendId, "Dream");
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(null, null), clock);
        AgentReply result = await runtime.RunAsync(conversation.ConversationId, "/dream inspect aliases", (_, _, _) => { }, CancellationToken.None, useTools: false);
        Assert.Contains("Allow tools", result.Text, StringComparison.Ordinal);
        Assert.Equal("blocked", result.Command!.Outcome);
        Assert.Empty(catalog.Agents.ReadToolCalls(conversation.ConversationId, 10));
    }

    [Fact]
    public async Task ConversationRecallSurvivesRestartAndDeletionAndNeverReachesRemoteModels()
    {
        Guid prior;
        using (SqliteCatalog first = SqliteCatalog.Open(root, clock))
        {
            first.Agents.SaveBackend(Local());
            prior = first.Agents.Create(Local().BackendId, "Orchid plans").ConversationId;
            first.Agents.Append(prior, AgentMessage.FromUser("Orchid coats ship Fridays."));
            first.Agents.AppendFinal(prior, AgentMessage.FromAssistant("Acknowledged.", []));
            clock.UtcNow += TimeSpan.FromSeconds(10);
            Assert.True(first.ChatMemory.SaveRecap(first.ChatMemory.Next()!, "Orchid coats ship Fridays; a generated private recap."));
        }

        using SqliteCatalog catalog = SqliteCatalog.Open(root, clock);
        Assert.Equal(prior, Assert.Single(catalog.ChatMemory.Search("Orchid", null, 3)).ConversationId);
        Assert.Empty(catalog.ChatMemory.Search("Orchid", prior, 3));
        Assert.Empty(catalog.ChatMemory.Search("\" OR *", null, 3));
        ScriptedBackend local = new(transcript =>
        {
            Assert.Contains(transcript, message => message.Content.Contains("generated private recap", StringComparison.Ordinal));
            return new AgentTurn("We discussed Orchid shipping.", [], 1, 1);
        });
        (AgentRuntime localRuntime, AgentConversation localChat) = Runtime(catalog, Local(), local);
        await localRuntime.RunAsync(localChat.ConversationId, "Remember Orchid?", (_, _, _) => { }, CancellationToken.None);

        ScriptedBackend remote = new(transcript =>
        {
            Assert.DoesNotContain(transcript, message => message.Content.Contains("generated private recap", StringComparison.Ordinal));
            return new AgentTurn("No previous conversation was disclosed.", [], 1, 1);
        });
        AgentBackendProfile cloud = new("remote", AgentBackendKinds.OpenAiCompatible, "https://api.example.com/v1", "model", null, null, true);
        (AgentRuntime remoteRuntime, AgentConversation remoteChat) = Runtime(catalog, cloud, remote);
        await remoteRuntime.RunAsync(remoteChat.ConversationId, "Remember Orchid?", (_, _, _) => { }, CancellationToken.None);
        Assert.True(catalog.Agents.Delete(prior));
        Assert.Empty(catalog.ChatMemory.Search("Orchid", null, 3));
    }

    [Fact]
    public async Task RevokedEvidenceSuppressesDerivedConversationRecall()
    {
        using SqliteCatalog catalog = await SeededAsync();
        var source = catalog.Library.List(10, 0, null).First();
        catalog.Agents.SaveBackend(Local());
        AgentConversation chat = catalog.Agents.Create(Local().BackendId, "Orchid evidence");
        catalog.Agents.Append(chat.ConversationId, AgentMessage.FromUser("Discuss Orchid."));
        catalog.Agents.AppendFinal(chat.ConversationId, AgentMessage.FromAssistant("Orchid evidence.", []) with
        {
            Citations = [new AgentCitation(source.SourceId, source.ItemId, source.Revision, "test", "source")]
        });
        clock.UtcNow += TimeSpan.FromSeconds(10);
        Assert.True(catalog.ChatMemory.SaveRecap(catalog.ChatMemory.Next()!, "Orchid evidence from the source."));
        Assert.Single(catalog.ChatMemory.Search("Orchid", null, 3));
        Assert.True(catalog.Sources.Revoke(source.SourceId));
        Assert.Empty(catalog.ChatMemory.Search("Orchid", null, 3));
    }
}
