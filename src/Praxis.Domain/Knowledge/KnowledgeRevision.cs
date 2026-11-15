namespace Praxis.Domain.Knowledge;

/// <summary>
/// One change Praxis made to, or proposes for, its own judgment about an entity: what changed,
/// what triggered the second look, and the evidence-based reason. <c>applied</c> revisions only
/// ever adjust model-inferred values; <c>suggested</c> ones wait for the owner.
/// </summary>
public sealed record KnowledgeRevision(
    long RevisionId, Guid EntityId, string EntityName, string Field, string? Before, string After,
    string Trigger, string Basis, string State, DateTimeOffset CreatedUtc, DateTimeOffset? DecidedUtc);
