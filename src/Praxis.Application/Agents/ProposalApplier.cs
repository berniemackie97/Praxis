using System.Text.Json;
using Praxis.Application.Knowledge;
using Praxis.Application.Policy;
using Praxis.Domain.Agents;
using Praxis.Domain.Knowledge;
using Praxis.Domain.Policy;

namespace Praxis.Application.Agents;

/// <summary>
/// Applies an accepted knowledge proposal through the same repositories the owner's UI uses.
/// Source-category proposals change what may be disclosed, so only the owner can apply them.
/// </summary>
public sealed class ProposalApplier(IKnowledgeRepository knowledge, IPolicyRepository policy, IAgentConversationRepository? conversations = null)
{
    public KnowledgeSnapshot? Basis(Guid entityId, Func<Guid, bool> mayDisclose)
    {
        Domain.Knowledge.EntityDetail? entity = knowledge.GetEntity(entityId);
        return entity is null || entity.Mentions.Count == 0 || entity.Mentions.Any(mention => !mayDisclose(mention.SourceId))
            ? null : KnowledgeSnapshot.Capture(entity);
    }

    public SourceCategory? CategoryBasis(Guid sourceId) => policy.ReadCategory(sourceId);

    public bool CanApplyDirectly(string kind) => kind != AgentProposal.SourceCategory;

    public bool IsKnownDefinition(string kind, string value) => knowledge.IsKnownDefinition(kind, value);

    public bool Apply(AgentProposal proposal)
    {
        using JsonDocument document = JsonDocument.Parse(proposal.PayloadJson);
        JsonElement payload = document.RootElement;
        KnowledgeSnapshot[] expected = payload.TryGetProperty("basis", out JsonElement basis)
            ? basis.Deserialize<KnowledgeSnapshot[]>() ?? [] : [];
        Guid Id(string name) => Guid.Parse(payload.GetProperty(name).GetString()!);
        string Text(string name) => payload.GetProperty(name).GetString()!;
        return knowledge.ApplyCurrent(expected, () =>
        {
            if (payload.TryGetProperty("ownerCorrectionId", out JsonElement ownerId) &&
                conversations?.ReadMemory(ownerId.GetGuid()) is not { Status: AgentMemory.Active or AgentMemory.Proposed })
            {
                return false;
            }
            if (proposal.Kind == AgentProposal.SourceCategory && payload.TryGetProperty("categoryBefore", out JsonElement categoryBefore) &&
                            categoryBefore.Deserialize<SourceCategory>() != policy.ReadCategory(Id("sourceId")))
            {
                return false;
            }
            return proposal.Kind switch
            {
                AgentProposal.EntityType => knowledge.SetType(Id("entityId"), Text("entityType")),
                AgentProposal.EntityRename => knowledge.Rename(Id("entityId"), Text("name")),
                AgentProposal.EntityMerge => knowledge.Merge(Id("entityId"), Id("intoEntityId")),
                AgentProposal.Relation => knowledge.Relate(Id("entityId"), Text("predicate"), Id("objectEntityId")) != Guid.Empty,
                AgentProposal.SourceCategory => Category(Id("sourceId"), Text("category")),
                _ => false
            };
        });
    }

    private bool Category(Guid sourceId, string category)
    {
        policy.SaveCategory(new SourceCategory(sourceId, category, SourceCategory.OwnerOrigin, policy.ReadCategory(sourceId)?.LocalOnly ?? false));
        return true;
    }
}
