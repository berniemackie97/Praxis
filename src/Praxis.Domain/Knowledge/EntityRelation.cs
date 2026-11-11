namespace Praxis.Domain.Knowledge;

public sealed record EntityRelation(
    Guid RelationId, string Predicate, bool Outgoing, Guid OtherEntityId, string OtherName,
    string Origin, string? Evidence);
