using Praxis.Domain.Agents;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>A conversation with its messages and tool-call audit.</summary>
public sealed record AgentConversationResponse(AgentConversation Conversation, IReadOnlyList<AgentMessage> Messages, IReadOnlyList<AgentToolCallRecord> ToolCalls);
