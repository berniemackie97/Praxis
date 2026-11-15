namespace Praxis.Domain.Agents;

public sealed record AgentConversation(Guid ConversationId, string BackendId, string Title, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc,
    string? Model = null, string? Effort = null);
