using System.Text.Json;
using Praxis.Application.Agents.Tools;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Capabilities;

/// <summary>Reports existing executables without starting them or disclosing filesystem paths.</summary>
public sealed class ProgramDiscoveryTool(IInstalledProgramProbe programs) : IAgentTool
{
    public const string ToolName = "capability_discover";

    public AgentToolDefinition Definition { get; } = new(ToolName,
        "Check whether a named command such as rea, node, npm, or brew is already installed. This reads availability, not permission to run it. Use before proposing installation.",
        ToolArguments.Schema("""{"type":"object","properties":{"program":{"type":"string"}},"required":["program"]}"""),
        ToolTiers.Read);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = new ToolArguments(arguments).RequireText("program", 64);
        if (!char.IsAsciiLetterOrDigit(name[0]) || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            throw new ArgumentException("Give a command name, not a path or a command line.");
        }

        return Task.FromResult(AgentToolOutput.Of(new { program = name, installed = programs.IsAvailable(name) }));
    }
}
