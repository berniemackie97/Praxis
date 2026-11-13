namespace Praxis.Domain.Agents;

/// <summary>A change the agent suggested to the owner's knowledge, awaiting review (or applied under allow_edits).</summary>
public sealed record AgentProposal(Guid ProposalId, Guid? ConversationId, string Kind, string PayloadJson, string Rationale, string Status, DateTimeOffset CreatedUtc)
{
    public const string Pending = "pending";
    public const string Applied = "applied";
    public const string Rejected = "rejected";

    public const string EntityType = "entity_type";
    public const string EntityRename = "entity_rename";
    public const string EntityMerge = "entity_merge";
    public const string Relation = "relation";
    public const string SourceCategory = "source_category";

    public static readonly IReadOnlyList<string> Kinds = [EntityType, EntityRename, EntityMerge, Relation, SourceCategory];
}
