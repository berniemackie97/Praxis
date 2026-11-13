namespace Praxis.Domain.Agents;

/// <summary>A tool invocation the model requested, with its JSON arguments as text.</summary>
public sealed record AgentToolCall(string CallId, string Name, string ArgumentsJson);
