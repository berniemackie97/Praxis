namespace Praxis.Domain.Agents;

/// <summary>
/// One persisted conversation message: user text, assistant text and/or tool calls, or one tool
/// result answering <see cref="ToolCallId"/>.
/// </summary>
public sealed record AgentMessage(string Role, string Content, IReadOnlyList<AgentToolCall> ToolCalls, string? ToolCallId, IReadOnlyList<AgentCitation> Citations, int? Sequence = null)
{
    public static AgentMessage FromUser(string text) => new(AgentRoles.User, text, [], null, []);

    public static AgentMessage FromAssistant(string text, IReadOnlyList<AgentToolCall> calls) => new(AgentRoles.Assistant, text, calls, null, []);

    public static AgentMessage FromTool(string callId, string content, IReadOnlyList<AgentCitation> citations) => new(AgentRoles.Tool, content, [], callId, citations);
}
