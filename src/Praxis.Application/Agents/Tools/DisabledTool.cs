using System.Text.Json;

namespace Praxis.Application.Agents.Tools;

/// <summary>A tool that is described to agents but never executes; the registry refuses its tier.</summary>
public sealed class DisabledTool(AgentToolDefinition definition) : IAgentTool
{
    public AgentToolDefinition Definition { get; } = definition;

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken) =>
        Task.FromResult(AgentToolOutput.Error("disabled", "This capability is not enabled in Praxis."));
}
