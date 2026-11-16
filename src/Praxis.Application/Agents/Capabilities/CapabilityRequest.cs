namespace Praxis.Application.Agents.Capabilities;

/// <summary>An immutable action request, and its owner-local execution receipt.</summary>
public sealed record CapabilityRequest(Guid Id, Guid ConversationId, string Operation, string? Target,
    string Description, string Status, string? Detail, DateTimeOffset CreatedUtc);
