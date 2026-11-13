namespace Praxis.Application.Policy;

/// <summary>Whether a requested change took effect or awaits owner confirmation (with its pending identifier).</summary>
public sealed record PolicyChangeOutcome(bool Applied, Guid? PendingChangeId);
