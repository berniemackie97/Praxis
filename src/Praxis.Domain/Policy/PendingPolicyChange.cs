namespace Praxis.Domain.Policy;

/// <summary>A widening change requested outside the owner's direct UI, waiting for confirmation.</summary>
public sealed record PendingPolicyChange(Guid ChangeId, PolicyChange Change, string RequestedBy, string Rationale, DateTimeOffset RequestedUtc);
