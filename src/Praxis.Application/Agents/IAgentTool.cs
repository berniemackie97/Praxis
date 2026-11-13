using System.Text.Json;

namespace Praxis.Application.Agents;

/// <summary>One Praxis capability exposed to agents. Arguments are untrusted model output and are validated by the tool.</summary>
public interface IAgentTool
{
    AgentToolDefinition Definition { get; }

    Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken);
}
