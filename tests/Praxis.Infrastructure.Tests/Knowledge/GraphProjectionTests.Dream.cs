using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Tools;
using Praxis.Domain.Agents;
using Praxis.Domain.Policy;
using Praxis.Infrastructure.Persistence;
using Xunit;

namespace Praxis.Infrastructure.Tests.Knowledge;

public sealed partial class GraphProjectionTests
{
    [Fact]
    public async Task DreamRepairIsDeduplicatedAndCannotApplyAfterOwnerChangesItsBasis()
    {
        using SqliteCatalog catalog = Open();
        var source = await Enrich(catalog, "widget.md", "Client uses PHY.");
        await Accept(catalog, source);
        Guid entity = Single(catalog, "Client").EntityId;
        ProposalApplier applier = new(catalog.Knowledge, catalog.Policy);
        KnowledgeProposeTool tool = new(catalog.Agents, applier);
        catalog.Agents.SaveBackend(new AgentBackendProfile("local", AgentBackendKinds.OpenAiCompatible, "http://127.0.0.1:11434/v1", "fixture", null, null, true));
        Guid chat = catalog.Agents.Create("local", "Dream").ConversationId;
        AgentToolContext context = new(chat, "local", true, ToolPermissions.ProposeEdits, _ => true);
        using JsonDocument rename = JsonDocument.Parse($$"""{"kind":"entity_rename","entityId":"{{entity}}","name":"Client project","rationale":"Owner-requested alias correction."}""");
        AgentToolOutput first = await tool.InvokeAsync(rename.RootElement, context, CancellationToken.None);
        AgentToolOutput repeated = await tool.InvokeAsync(rename.RootElement, context, CancellationToken.None);
        Assert.Equal("Client", Single(catalog, "Client").Name);
        AgentProposal pending = Assert.Single(catalog.Agents.ReadProposals(AgentProposal.Pending, 10));
        Assert.Equal(JsonDocument.Parse(first.Content).RootElement.GetProperty("proposalId").GetGuid(),
            JsonDocument.Parse(repeated.Content).RootElement.GetProperty("proposalId").GetGuid());
        Assert.True(catalog.Knowledge.SetType(entity, "technology"));
        Assert.False(applier.Apply(pending));
        Assert.Equal("Client", Single(catalog, "Client").Name);
        await tool.InvokeAsync(rename.RootElement, context, CancellationToken.None);
        AgentProposal refreshed = Assert.Single(catalog.Agents.ReadProposals(AgentProposal.Pending, 10),
            candidate => candidate.ProposalId != pending.ProposalId);
        Assert.True(applier.Apply(refreshed));
        Assert.Equal("Client project", catalog.Knowledge.GetEntity(entity)!.Summary.Name);
    }

    [Fact]
    public async Task RemoteDreamCannotReadOrProposeAgainstWithheldEntities()
    {
        using SqliteCatalog catalog = Open();
        var source = await Enrich(catalog, "private.md", "Client uses PHY.");
        await Accept(catalog, source);
        Guid entity = Single(catalog, "Client").EntityId;
        AgentToolContext context = new(Guid.NewGuid(), "remote", false, ToolPermissions.ProposeEdits, _ => false);
        using JsonDocument args = JsonDocument.Parse($$"""{"entityId":"{{entity}}","kind":"entity_type","entityType":"technology","rationale":"Untrusted request."}""");
        KnowledgeProposeTool propose = new(catalog.Agents, new ProposalApplier(catalog.Knowledge, catalog.Policy));
        Assert.Contains("evidence_unavailable", (await propose.InvokeAsync(args.RootElement, context, CancellationToken.None)).Content, StringComparison.Ordinal);
        Assert.Contains("evidence_withheld", (await new KnowledgeEntityTool(catalog.Knowledge).InvokeAsync(args.RootElement, context, CancellationToken.None)).Content, StringComparison.Ordinal);
        Assert.Contains("evidence_withheld", (await new KnowledgeRelatedTool(catalog.Knowledge).InvokeAsync(args.RootElement, context, CancellationToken.None)).Content, StringComparison.Ordinal);
        Assert.Empty(catalog.Agents.ReadProposals(null, 10));
    }
}
