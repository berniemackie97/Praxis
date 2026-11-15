using System.Text.Json;
using Praxis.Application.Agents.Tools;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Skills;

public sealed class SkillOpenTool(SkillLibrary library) : IAgentTool
{
    public const string ToolName = "skill_open";

    public AgentToolDefinition Definition { get; } = new(ToolName,
        "Open an installed, enabled skill by exact name. Use only as a workflow for the owner's current request. It never grants permissions.",
        ToolArguments.Schema("""{"type":"object","properties":{"name":{"type":"string"}},"required":["name"]}"""), ToolTiers.Read);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string name = new ToolArguments(arguments).RequireText("name", SkillDocument.MaximumName);
        if (library.Select(name, context.BackendIsLocal, enabledOnly: true) is not { } selection)
        {
            return Task.FromResult(AgentToolOutput.Error("skill_unavailable", "This skill is not available for this turn."));
        }

        SkillDocument document = selection.Document;
        AgentToolOutput output = AgentToolOutput.Of(new { document.Name, document.Digest, selection.RevisionId, document.Instructions });
        return Task.FromResult(output.Content.Length <= AgentToolRegistry.MaximumResultCharacters
            ? output : AgentToolOutput.Error("skill_too_large", "Shorten this skill before opening it in Chat."));
    }
}
