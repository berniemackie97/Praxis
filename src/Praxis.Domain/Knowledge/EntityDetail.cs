namespace Praxis.Domain.Knowledge;

public sealed record EntityDetail(
    EntitySummary Summary, IReadOnlyList<string> Aliases, IReadOnlyList<EntityMention> Mentions,
    IReadOnlyList<EntityRelation> Relations, IReadOnlyList<RelatedEntity> Related);
