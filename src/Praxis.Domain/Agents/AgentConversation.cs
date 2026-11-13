namespace Praxis.Domain.Agents;

public sealed record AgentConversation(Guid ConversationId, string BackendId, string Title, DateTimeOffset CreatedUtc, DateTimeOffset UpdatedUtc);
