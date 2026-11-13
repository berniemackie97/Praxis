namespace Praxis.Application.Agents.Tools;

/// <summary>
/// System actions are part of the tool surface so agents can describe them, but every call is
/// refused by the runtime until a separately approved system permission exists (#297).
/// </summary>
public static class SystemActionTools
{
    public static IReadOnlyList<IAgentTool> All { get; } =
    [
        new DisabledTool(new AgentToolDefinition("system_open_url",
            "Open a URL or search in the owner's preferred browser. Currently disabled: explain the link instead.",
            ToolArguments.Schema("""{"type":"object","properties":{"url":{"type":"string"}},"required":["url"]}"""), Domain.Agents.ToolTiers.System)),
        new DisabledTool(new AgentToolDefinition("system_run_command",
            "Run a sandboxed terminal command. Currently disabled: describe the command for the owner to run.",
            ToolArguments.Schema("""{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]}"""), Domain.Agents.ToolTiers.System))
    ];
}
