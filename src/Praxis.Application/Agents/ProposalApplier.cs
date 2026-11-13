using System.Text.Json;
using Praxis.Application.Knowledge;
using Praxis.Application.Policy;
using Praxis.Domain.Agents;
using Praxis.Domain.Policy;

namespace Praxis.Application.Agents;

/// <summary>
/// Applies an accepted knowledge proposal through the same repositories the owner's UI uses.
/// Source-category proposals change what may be disclosed, so only the owner can apply them.
/// </summary>
public sealed class ProposalApplier(IKnowledgeRepository knowledge, IPolicyRepository policy)
{
    public bool CanApplyDirectly(string kind) => kind != AgentProposal.SourceCategory;

    public bool Apply(AgentProposal proposal)
    {
        using JsonDocument document = JsonDocument.Parse(proposal.PayloadJson);
        JsonElement payload = document.RootElement;
        Guid Id(string name) => Guid.Parse(payload.GetProperty(name).GetString()!);
        string Text(string name) => payload.GetProperty(name).GetString()!;
        return proposal.Kind switch
        {
            AgentProposal.EntityType => knowledge.SetType(Id("entityId"), Text("entityType")),
            AgentProposal.EntityRename => knowledge.Rename(Id("entityId"), Text("name")),
            AgentProposal.EntityMerge => knowledge.Merge(Id("entityId"), Id("intoEntityId")),
            AgentProposal.Relation => knowledge.Relate(Id("entityId"), Text("predicate"), Id("objectEntityId")) != Guid.Empty,
            AgentProposal.SourceCategory => Category(Id("sourceId"), Text("category")),
            _ => false
        };
    }

    private bool Category(Guid sourceId, string category)
    {
        policy.SaveCategory(new SourceCategory(sourceId, category, SourceCategory.OwnerOrigin, policy.ReadCategory(sourceId)?.LocalOnly ?? false));
        return true;
    }
}
