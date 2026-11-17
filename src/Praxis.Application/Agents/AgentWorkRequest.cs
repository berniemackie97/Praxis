namespace Praxis.Application.Agents;

public sealed record AgentWorkRequest(Guid RunId, Guid ConversationId, string Request, bool UseTools, string Kind, DateTimeOffset CreatedUtc);
