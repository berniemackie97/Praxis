namespace Praxis.Domain.Knowledge;

/// <summary>
/// One entity with its evidence. <paramref name="Members"/> and <paramref name="MemberOf"/> follow the
/// naming convention of the evidence (<c>CRadar_Show</c> is a member of <c>CRadar</c>), worked out
/// when read, so knowledge captured before membership was extracted is still connected.
/// </summary>
public sealed record EntityDetail(
    EntitySummary Summary, IReadOnlyList<string> Aliases, IReadOnlyList<EntityMention> Mentions,
    IReadOnlyList<EntityRelation> Relations, IReadOnlyList<RelatedEntity> Related,
    IReadOnlyList<RelatedEntity>? Members = null, RelatedEntity? MemberOf = null);
