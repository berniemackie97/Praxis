using System.Text.Json;
using System.Text.Json.Nodes;
using Praxis.Domain.Agents;
using Praxis.Domain.Knowledge;
using Praxis.Domain.Policy;

namespace Praxis.Application.Agents.Tools;

/// <summary>
/// Suggests a correction to the owner's knowledge: an entity's type or name, merging duplicates,
/// a relation, or a source's data category. With allow_edits knowledge changes apply at once and
/// are recorded; otherwise (and always for categories) they wait for owner review.
/// </summary>
public sealed class KnowledgeProposeTool(IAgentConversationRepository repository, ProposalApplier applier) : IAgentTool
{
    public AgentToolDefinition Definition { get; } = new("knowledge_propose",
        $$"""
        Propose a correction: kind = entity_type {entityId, entityType} | entity_rename {entityId, name} | entity_merge {entityId, intoEntityId} |
        relation {entityId, predicate, objectEntityId} | source_category {sourceId, category}. Entity types: {{string.Join(", ", KnowledgeVocabulary.EntityTypes)}}.
        Predicates: {{string.Join(", ", KnowledgeVocabulary.Predicates)}}. Always give a rationale citing evidence. Applies immediately only if the
        owner allows edits (never for source_category); otherwise it is queued for the owner to review.
        """,
        ToolArguments.Schema("""
            {"type":"object","properties":{"kind":{"type":"string","enum":["entity_type","entity_rename","entity_merge","relation","source_category"]},
              "entityId":{"type":"string"},"entityType":{"type":"string"},"name":{"type":"string"},"intoEntityId":{"type":"string"},
              "predicate":{"type":"string"},"objectEntityId":{"type":"string"},"sourceId":{"type":"string"},"category":{"type":"string"},
              "rationale":{"type":"string"}},"required":["kind","rationale"]}
            """), ToolTiers.Propose);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ToolArguments args = new(arguments);
        string kind = args.Choice("kind", AgentProposal.Kinds, "");
        object payload = kind switch
        {
            AgentProposal.EntityType => new { entityId = args.RequireGuid("entityId"), entityType = Vocabulary(args.RequireText("entityType", 32), KnowledgeVocabulary.EntityTypes, "entityType") },
            AgentProposal.EntityRename => new { entityId = args.RequireGuid("entityId"), name = args.RequireText("name", 128) },
            AgentProposal.EntityMerge => new { entityId = args.RequireGuid("entityId"), intoEntityId = args.RequireGuid("intoEntityId") },
            AgentProposal.Relation => new { entityId = args.RequireGuid("entityId"), predicate = Vocabulary(args.RequireText("predicate", 32), KnowledgeVocabulary.Predicates, "predicate"), objectEntityId = args.RequireGuid("objectEntityId") },
            AgentProposal.SourceCategory => new { sourceId = args.RequireGuid("sourceId"), category = Vocabulary(args.RequireText("category", 32), DataCategories.All, "category") },
            _ => throw new ArgumentException($"'kind' must be one of: {string.Join(", ", AgentProposal.Kinds)}.")
        };
        JsonObject fenced = JsonSerializer.SerializeToNode(payload)!.AsObject();
        List<KnowledgeProposalBasis> basis = [];
        foreach (string field in new[] { "entityId", "intoEntityId", "objectEntityId" })
        {
            if (fenced[field] is not { } target)
            {
                continue;
            }
            if (applier.Basis(target.GetValue<Guid>(), context.MayDisclose) is not { } observed)
            {
                return Task.FromResult(AgentToolOutput.Error("evidence_unavailable", "The target has no fully disclosable current evidence. No proposal was created."));
            }
            basis.Add(observed);
        }
        fenced["basis"] = JsonSerializer.SerializeToNode(basis);
        bool direct = applier.CanApplyDirectly(kind) && ToolTiers.Permits(context.ToolPermission, ToolTiers.Write);
        AgentProposal proposal = repository.AddProposal(context.ConversationId, kind, fenced.ToJsonString(), args.RequireText("rationale", 1_000), AgentProposal.Pending);
        bool applied = direct && applier.Apply(proposal);
        if (applied)
        {
            repository.SetProposalStatus(proposal.ProposalId, AgentProposal.Pending, AgentProposal.Applied);
        }
        return Task.FromResult(AgentToolOutput.Of(new
        {
            proposal.ProposalId,
            before = basis.Select(item => new { item.EntityId, item.Name, item.Type }),
            proposed = payload,
            status = applied ? "applied" : direct ? "apply_failed" : "awaiting_owner_review"
        }));
    }

    private static string Vocabulary(string value, IEnumerable<string> allowed, string name) =>
        allowed.Contains(value, StringComparer.Ordinal) ? value : throw new ArgumentException($"'{name}' must be one of: {string.Join(", ", allowed)}.");
}
