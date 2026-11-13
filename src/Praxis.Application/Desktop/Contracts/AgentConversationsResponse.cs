using Praxis.Domain.Agents;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>Recent conversations.</summary>
public sealed record AgentConversationsResponse(IReadOnlyList<AgentConversation> Conversations);
