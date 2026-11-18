using Praxis.Application.Agents;
using Praxis.Application.Agents.Tools;
using Praxis.Domain.Agents;
using Praxis.Domain.Knowledge;
using Praxis.Domain.Policy;
using Praxis.Infrastructure.Persistence;
using Xunit;

namespace Praxis.Infrastructure.Tests.Knowledge;

public sealed partial class GraphProjectionTests
{
    [Fact]
    public async Task UnchangedDreamReviewsSurviveRestartButChangedEvidenceBecomesEligibleAgain()
    {
        Guid entity, conversation;
        KnowledgeSnapshot reviewed;
        using (SqliteCatalog catalog = Open())
        {
            var source = await Enrich(catalog, "reviewed.md", "Client uses PHY."); await Accept(catalog, source);
            entity = Single(catalog, "Client").EntityId;
            reviewed = KnowledgeSnapshot.Capture(catalog.Knowledge.GetEntity(entity)!);
            catalog.Agents.SaveBackend(new("local", AgentBackendKinds.OpenAiCompatible, "http://127.0.0.1:11434/v1", "fixture", null, null, true));
            conversation = catalog.Agents.Create("local", "Review").ConversationId;
            Guid run = Guid.NewGuid();
            catalog.AgentRuns.Enqueue(run, conversation, "/dream", true, "dream"); Assert.True(catalog.AgentRuns.TryStart(run));
            catalog.Agents.Append(conversation, AgentMessage.FromUser("/dream"));
            catalog.Agents.AppendFinal(conversation, AgentMessage.FromAssistant("No supported repair.", []));
            catalog.AgentRuns.Finish(run, AgentRunStatus.Completed, new AgentReply(conversation, "No supported repair.", AgentReply.Completed,
                [], [], 0, new AnswerCheck([], false, false), 0, 0,
                Command: new AgentCommandReceipt("dream", "reviewed", 1, [reviewed], [], 0, "Review recorded.")), null);
            Assert.True(catalog.AgentRuns.RecentlyReviewed(reviewed));
        }
        using SqliteCatalog reopened = Open();
        Assert.True(reopened.AgentRuns.RecentlyReviewed(reviewed));
        Assert.Single(reopened.AgentRuns.CommandHistory(conversation, 10));
        Assert.True(reopened.Knowledge.SetType(entity, "technology"));
        Assert.False(reopened.AgentRuns.RecentlyReviewed(KnowledgeSnapshot.Capture(reopened.Knowledge.GetEntity(entity)!)));
    }

    [Fact]
    public async Task RejectingAnOwnerCorrectionBlocksItsPendingGraphRepair()
    {
        using SqliteCatalog catalog = Open();
        var source = await Enrich(catalog, "rejected.md", "Client uses PHY."); await Accept(catalog, source);
        Guid entity = Single(catalog, "Client").EntityId;
        string? before = catalog.Knowledge.GetEntity(entity)!.Summary.EntityType;
        catalog.Agents.SaveBackend(new("local", AgentBackendKinds.OpenAiCompatible, "http://127.0.0.1:11434/v1", "fixture", null, null, true));
        Guid chat = catalog.Agents.Create("local", "Correction provenance").ConversationId;
        AgentMemory memory = catalog.Agents.SaveOwnerCorrection(chat, "Client is a technology. Update your brain.", false);
        var applier = new Praxis.Application.Agents.ProposalApplier(catalog.Knowledge, catalog.Policy, catalog.Agents);
        var context = new AgentToolContext(chat, "local", true, ToolPermissions.ProposeEdits, _ => true, OwnerCorrectionId: memory.MemoryId);
        using var args = System.Text.Json.JsonDocument.Parse($$"""{"kind":"entity_type","entityId":"{{entity}}","entityType":"technology","rationale":"Owner correction."}""");
        await new KnowledgeProposeTool(catalog.Agents, applier).InvokeAsync(args.RootElement, context, CancellationToken.None);
        AgentProposal pending = Assert.Single(catalog.Agents.ReadProposals(AgentProposal.Pending, 10));
        Assert.True(catalog.Agents.SetMemoryStatus(memory.MemoryId, AgentMemory.Rejected));
        Assert.False(applier.Apply(pending)); Assert.Equal(before, catalog.Knowledge.GetEntity(entity)!.Summary.EntityType);
    }

    [Fact]
    public async Task OwnerCorrectionCannotOverwriteAnOwnerChangeMadeDuringMapping()
    {
        using SqliteCatalog catalog = Open();
        var source = await Enrich(catalog, "race.md", "Client uses PHY."); await Accept(catalog, source);
        Guid id = Single(catalog, "Client").EntityId;
        KnowledgeSnapshot expected = KnowledgeSnapshot.Capture(catalog.Knowledge.GetEntity(id)!);
        catalog.Agents.SaveBackend(new("local", AgentBackendKinds.OpenAiCompatible, "http://127.0.0.1:11434/v1", "fixture", null, null, true));
        Guid chat = catalog.Agents.Create("local", "Correction race").ConversationId;
        Assert.True(catalog.Knowledge.SetType(id, "organization"));
        AgentToolContext context = new(chat, "local", true, ToolPermissions.AllowEdits, _ => true, ExpectedKnowledge: new Dictionary<Guid, KnowledgeSnapshot> { [id] = expected });
        using var args = System.Text.Json.JsonDocument.Parse($$"""{"kind":"entity_type","entityId":"{{id}}","entityType":"technology","rationale":"Stale mapping."}""");
        var result = await new KnowledgeProposeTool(catalog.Agents, new Praxis.Application.Agents.ProposalApplier(catalog.Knowledge, catalog.Policy)).InvokeAsync(args.RootElement, context, CancellationToken.None);
        Assert.Contains("evidence_changed", result.Content, StringComparison.Ordinal);
        Assert.Equal("organization", catalog.Knowledge.GetEntity(id)!.Summary.EntityType);
        Assert.Empty(catalog.Agents.ReadProposals(null, 10));
    }
}
