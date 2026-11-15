namespace Praxis.Domain.Knowledge;

/// <summary>
/// One change Praxis made to, or proposes for, its own judgment about an entity: what changed,
/// what triggered the second look, and the evidence-based reason. <c>applied</c> revisions only
/// ever adjust model-inferred values; <c>suggested</c> ones wait for the owner. Field is
/// <c>entity_type</c>, <c>merge</c> (After is the target entity id) or <c>relation</c> (After is the
/// proposed action for RelationId); AfterLabel is the readable form: the type, the merge target's
/// name, or the relation concerned.
/// </summary>
public sealed record KnowledgeRevision(
    long RevisionId, Guid EntityId, string EntityName, string Field, Guid? RelationId, string? Before, string After,
    string AfterLabel, string Trigger, string Basis, string State, DateTimeOffset CreatedUtc, DateTimeOffset? DecidedUtc);
