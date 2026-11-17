using Praxis.Domain.Agents;

namespace Praxis.Application.Agents;

/// <summary>One backend completion: assistant text and/or tool calls, with reported token usage.</summary>
public sealed record AgentTurn(string Text, IReadOnlyList<AgentToolCall> ToolCalls, int InputTokens, int OutputTokens,
    AgentRequestFootprint? Request = null);
