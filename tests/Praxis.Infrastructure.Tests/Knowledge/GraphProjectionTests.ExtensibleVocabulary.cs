using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Runtime;
using Praxis.Application.Knowledge;
using Praxis.Domain.Agents;
using Praxis.Domain.Policy;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Persistence;
using Xunit;

namespace Praxis.Infrastructure.Tests.Knowledge;

public sealed partial class GraphProjectionTests
{
    [Fact]
    public async Task ApprovedNewDefinitionsKeepGameAndSoftwareProjectDistinct()
    {
        using SqliteCatalog catalog = Open();
        types["Conquer Online"] = "project";
        types["OpenConquer"] = "project";
        var source = await Enrich(catalog, "entities.md", "Conquer Online is a game. OpenConquer models Conquer Online.");
        await Accept(catalog, source);

        Guid game = Single(catalog, "Conquer Online").EntityId;
        Guid project = Single(catalog, "OpenConquer").EntityId;
        Assert.NotEqual(game, project);
        Assert.False(catalog.Knowledge.IsKnownDefinition("entity_type", "game"));
        Assert.False(catalog.Knowledge.IsKnownDefinition("predicate", "models"));

        Assert.True(catalog.Knowledge.SetType(game, "game"));
        Guid link = catalog.Knowledge.Relate(project, "models", game);
        Assert.NotEqual(Guid.Empty, link);

        Assert.True(catalog.Knowledge.IsKnownDefinition("entity_type", "game"));
        Assert.True(catalog.Knowledge.IsKnownDefinition("predicate", "models"));
        Assert.Equal("game", catalog.Knowledge.GetEntity(game)!.Summary.EntityType);
        Assert.Equal("project", catalog.Knowledge.GetEntity(project)!.Summary.EntityType);
        Assert.Equal(game, Assert.Single(catalog.Graph.Seeds(null, new GraphQuery(EntityType: "game")).Nodes).EntityId);
        Assert.Contains(catalog.Graph.Zones(new GraphQuery(Predicate: "models")).Links,
            edge => edge.SubjectType == "project" && edge.ObjectType == "game" && edge.ConfirmedCount == 1);
        Assert.Throws<ArgumentException>(() => catalog.Knowledge.SetType(game, "game/product"));
        Assert.Throws<ArgumentException>(() => catalog.Knowledge.Relate(project, "DROP TABLE", game));
    }

    [Fact]
    public async Task NovelCorrectionIsReviewableEvenWhenOrdinaryEditsArePermitted()
    {
        using SqliteCatalog catalog = Open();
        types["Conquer Online"] = "project";
        types["OpenConquer"] = "project";
        var source = await Enrich(catalog, "correction.md", "Conquer Online is a game. OpenConquer is a project.");
        await Accept(catalog, source);
        Guid game = Single(catalog, "Conquer Online").EntityId;
        Guid project = Single(catalog, "OpenConquer").EntityId;

        catalog.Policy.ApplyChange(new PolicyChange(ToolPermission: ToolPermissions.AllowEdits), PolicyActors.Owner);
        AgentBackendProfile profile = new("ollama", AgentBackendKinds.OpenAiCompatible,
            "http://127.0.0.1:11434/v1", "plain-local", null, null, true);
        catalog.Agents.SaveBackend(profile);
        Guid chat = catalog.Agents.Create(profile.BackendId, "New classification").ConversationId;
        const string statement = "Conquer Online is a game. OpenConquer is a software project. Update your brain with that.";
        var mapping = JsonSerializer.Serialize(new
        {
            changes = new[]
        {
            new { kind = "entity_type", entityId = game, entityType = "game", quote = statement }
        }
        });
        AgentRuntime runtime = AgentComposition.Runtime(catalog, [], new DynamicMappingModel(mapping), TimeProvider.System);
        AgentReply result = await runtime.RunAsync(chat, statement, (_, _, _) => { }, CancellationToken.None);

        Assert.Equal(0, result.Command!.AppliedChanges);
        Assert.Equal("project", catalog.Knowledge.GetEntity(game)!.Summary.EntityType);
        Assert.False(catalog.Knowledge.IsKnownDefinition("entity_type", "game"));
        AgentProposal pending = Assert.Single(catalog.Agents.ReadProposals(AgentProposal.Pending, 10));
        Assert.Equal(AgentProposal.EntityType, pending.Kind);
        Assert.True(new ProposalApplier(catalog.Knowledge, catalog.Policy, catalog.Agents).Apply(pending));
        Assert.Equal("game", catalog.Knowledge.GetEntity(game)!.Summary.EntityType);
        Assert.Equal("project", catalog.Knowledge.GetEntity(project)!.Summary.EntityType);
        Assert.True(catalog.Knowledge.IsKnownDefinition("entity_type", "game"));
    }

    private sealed class DynamicMappingModel(string reply) : IAgentBackendFactory, IAgentBackend
    {
        public IAgentBackend CreateTurnBackend(AgentBackendProfile profile) => this;
        public ISessionAgentBackend? CreateSessionBackend(AgentBackendProfile profile) => null;
        public ValueTask<bool> SupportsToolsAsync(CancellationToken token) => ValueTask.FromResult(false);
        public Task<AgentTurn> CompleteAsync(string system, IReadOnlyList<AgentMessage> transcript,
            IReadOnlyList<AgentToolDefinition> tools, CancellationToken token) =>
            Task.FromResult(new AgentTurn(reply, [], 30, 20));
    }
}
