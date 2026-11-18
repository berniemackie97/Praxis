using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Memory;
using Praxis.Application.Agents.Runtime;
using Praxis.Domain.Agents;
using Praxis.Domain.Policy;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Persistence;
using Xunit;

namespace Praxis.Infrastructure.Tests.Knowledge;

public sealed partial class GraphProjectionTests
{
    private const string OwnerCorrection = "Conquer Online is a game. OpenConquer is an emulation project for that game. Update your brain with that.";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OwnerCanCorrectRelatedDistinctEntitiesUsingAModelWithoutToolSupport(bool apply)
    {
        using SqliteCatalog catalog = Open();
        types["Conquer Online"] = "project"; types["OpenConquer"] = "project";
        var source = await Enrich(catalog, "context.md", "Conquer Online and OpenConquer are discussed here.");
        await Accept(catalog, source);
        Guid game = Single(catalog, "Conquer Online").EntityId, project = Single(catalog, "OpenConquer").EntityId;
        catalog.Policy.ApplyChange(new PolicyChange(ToolPermission: apply ? ToolPermissions.AllowEdits : ToolPermissions.ProposeEdits), PolicyActors.Owner);
        AgentBackendProfile profile = new("ollama", AgentBackendKinds.OpenAiCompatible, "http://127.0.0.1:11434/v1", "plain-local", null, null, true);
        catalog.Agents.SaveBackend(profile);
        Guid chat = catalog.Agents.Create(profile.BackendId, "Owner correction").ConversationId;
        CorrectionModel model = new(JsonSerializer.Serialize(new { changes = new[] { new { kind = "relation", entityId = project, predicate = "implements", objectEntityId = game, quote = OwnerCorrection } } }));
        AgentRuntime runtime = AgentComposition.Runtime(catalog, [], model, TimeProvider.System);
        AgentReply reply = await runtime.RunAsync(chat, OwnerCorrection, (_, _, _) => { }, CancellationToken.None);
        Assert.True(reply.Check.Clean); Assert.Equal(1, model.Calls); Assert.Equal(OwnerCorrection, Assert.Single(catalog.Agents.ReadMemories(null, null, 10)).OwnerStatement);
        Assert.Equal(2, catalog.Knowledge.ListEntities(null, null, 10).Count);
        Assert.Equal(apply ? 1 : 0, reply.Command!.AppliedChanges);
        Assert.Equal(apply ? "applied" : "proposals_ready", reply.Command.Outcome);
        if (apply)
        {
            Assert.Contains(catalog.Knowledge.GetEntity(project)!.Relations, relation => relation.Predicate == "implements" && relation.OtherEntityId == game);
            Assert.Single(catalog.Agents.FindOwnerCorrections("OpenConquer", 3));
            AgentReply repeated = await runtime.RunAsync(chat, OwnerCorrection, (_, _, _) => { }, CancellationToken.None);
            Assert.Equal("already_current", repeated.Command!.Outcome);
            Assert.Equal(0, repeated.Command.AppliedChanges);
            Assert.Single(catalog.Agents.ReadMemories(null, null, 10));
        }
        else
        {
            Assert.Single(catalog.Agents.ReadProposals(AgentProposal.Pending, 10));
            Assert.Empty(catalog.Agents.FindOwnerCorrections("OpenConquer", 3));
            AgentMemory memory = Assert.Single(catalog.Agents.ReadMemories(AgentMemory.Proposed, null, 10));
            Assert.True(catalog.Agents.SetMemoryStatus(memory.MemoryId, AgentMemory.Active));
            Assert.Single(catalog.Agents.FindOwnerCorrections("OpenConquer", 3));
        }
        Assert.Single(catalog.Agents.ReadToolCalls(chat, 10));
    }

    [Fact]
    public async Task InvalidCorrectionMappingStillPreservesTheExactOwnerStatementWithoutChangingGraph()
    {
        using SqliteCatalog catalog = Open();
        types["OpenConquer"] = "project";
        var source = await Enrich(catalog, "mapping.md", "OpenConquer has its own identity."); await Accept(catalog, source);
        catalog.Policy.ApplyChange(new PolicyChange(ToolPermission: ToolPermissions.AllowEdits), PolicyActors.Owner);
        AgentBackendProfile profile = new("ollama", AgentBackendKinds.OpenAiCompatible, "http://127.0.0.1:11434/v1", "plain-local", null, null, true);
        catalog.Agents.SaveBackend(profile);
        Guid chat = catalog.Agents.Create(profile.BackendId, "Correction").ConversationId;
        CorrectionModel model = new("not valid JSON");
        AgentRuntime runtime = AgentComposition.Runtime(catalog, [], model, TimeProvider.System);
        AgentReply result = await runtime.RunAsync(chat, OwnerCorrection, (_, _, _) => { }, CancellationToken.None);
        Assert.Equal("recorded_unmapped", result.Command!.Outcome);
        Assert.Equal(0, result.Command.AppliedChanges); Assert.Empty(catalog.Agents.ReadProposals(null, 10));
        Assert.Equal(OwnerCorrection, Assert.Single(catalog.Agents.FindOwnerCorrections("OpenConquer", 3)).OwnerStatement);
        Assert.False(OwnerCorrectionService.IsRequest("Do not update your brain with that."));
        Assert.False(OwnerCorrectionService.IsRequest("What do you remember about my memory?"));
    }

    [Fact]
    public async Task PermissionRevokedDuringCorrectionMappingStopsLaterGraphEffects()
    {
        using SqliteCatalog catalog = Open();
        types["Conquer Online"] = "project"; types["OpenConquer"] = "project";
        var source = await Enrich(catalog, "permission.md", "Conquer Online and OpenConquer are separate."); await Accept(catalog, source);
        Guid game = Single(catalog, "Conquer Online").EntityId, project = Single(catalog, "OpenConquer").EntityId;
        catalog.Policy.ApplyChange(new PolicyChange(ToolPermission: ToolPermissions.AllowEdits), PolicyActors.Owner);
        AgentBackendProfile profile = new("ollama", AgentBackendKinds.OpenAiCompatible, "http://127.0.0.1:11434/v1", "plain-local", null, null, true);
        catalog.Agents.SaveBackend(profile);
        Guid chat = catalog.Agents.Create(profile.BackendId, "Permission change").ConversationId;
        CorrectionModel model = new(JsonSerializer.Serialize(new { changes = new[] { new { kind = "relation", entityId = project, predicate = "implements", objectEntityId = game, quote = OwnerCorrection } } }))
        { DuringCompletion = () => catalog.Policy.ApplyChange(new PolicyChange(ToolPermission: ToolPermissions.ReadOnly), PolicyActors.Owner) };
        AgentRuntime runtime = AgentComposition.Runtime(catalog, [], model, TimeProvider.System);
        AgentReply result = await runtime.RunAsync(chat, OwnerCorrection, (_, _, _) => { }, CancellationToken.None);
        Assert.Equal(0, result.Command!.AppliedChanges); Assert.Empty(catalog.Agents.ReadProposals(null, 10));
        Assert.DoesNotContain(catalog.Knowledge.GetEntity(project)!.Relations, relation => relation.Predicate == "implements");
        Assert.Single(catalog.Agents.ReadMemories(AgentMemory.Active, null, 10)); // This explicit statement was saved before revocation.
    }

    [Theory]
    [InlineData("conquer online is a game Openconquer is an emulation project for that game right now in memory you have those as seperate regions that should be corrected")]
    [InlineData("conquer online is a game Openconquer is an emulation project for that game right now in memory you have those as seperate regions that should be corrected i need you to update your brain with that")]
    public void NaturalOwnerCorrectionsDoNotRequireSpecialSyntax(string statement) => Assert.True(OwnerCorrectionService.IsRequest(statement));

    private sealed class CorrectionModel(string response) : IAgentBackendFactory, IAgentBackend
    {
        public int Calls { get; private set; }
        public Action? DuringCompletion { get; init; }
        public IAgentBackend CreateTurnBackend(AgentBackendProfile profile) => this;
        public ISessionAgentBackend? CreateSessionBackend(AgentBackendProfile profile) => null;
        public ValueTask<bool> SupportsToolsAsync(CancellationToken token) => ValueTask.FromResult(false);
        public Task<AgentTurn> CompleteAsync(string system, IReadOnlyList<AgentMessage> transcript, IReadOnlyList<AgentToolDefinition> tools, CancellationToken token)
        {
            Calls++; Assert.Empty(tools); Assert.Contains("ownerStatement", Assert.Single(transcript).Content, StringComparison.Ordinal);
            DuringCompletion?.Invoke();
            return Task.FromResult(new AgentTurn(response, [], 40, 20));
        }
    }
}
