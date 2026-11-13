using Praxis.Domain.Agents;

namespace Praxis.Application.Agents;

/// <summary>
/// A CLI agent (Claude Code, Codex) that runs its own loop and reaches Praxis tools through the
/// MCP relay. Praxis executes every tool call through the invoke callback, so policy,
/// permissions, audit and citations are identical to Praxis-driven backends.
/// </summary>
public interface ISessionAgentBackend
{
    Task<AgentTurn> RunAsync(string systemPrompt, IReadOnlyList<AgentMessage> transcript, IReadOnlyList<AgentToolDefinition> tools,
        Func<AgentToolCall, CancellationToken, Task<string>> invoke, CancellationToken cancellationToken);
}
