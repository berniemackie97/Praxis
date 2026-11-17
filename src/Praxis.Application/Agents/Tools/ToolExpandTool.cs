using System.Text.Json;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Tools;

public sealed class ToolExpandTool : IAgentTool
{
    public const string ToolName = "tools_expand";

    public AgentToolDefinition Definition { get; } = new(ToolName,
        "Restore the full permitted tool catalog on the next model turn when you need a tool not currently available. Does not change permissions.",
        ToolArguments.Schema("""{"type":"object","properties":{}}"""), ToolTiers.Read);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(AgentToolOutput.Of(new { expanded = true }));
    }
}
