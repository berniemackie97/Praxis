using System.Text.Json;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Tools;

/// <summary>Records a preference or standing fact the owner stated; active at once only with allow_edits, otherwise proposed.</summary>
public sealed class MemoryRememberTool(IAgentConversationRepository repository) : IAgentTool
{
    public AgentToolDefinition Definition { get; } = new("memory_remember",
        "Remember a durable preference or fact the owner explicitly stated (one short sentence). Never store secrets, credentials or account numbers. Becomes active immediately only if the owner allows edits; otherwise it awaits review.",
        ToolArguments.Schema("""{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}"""), ToolTiers.Propose);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        string text = new ToolArguments(arguments).RequireText("text", 300);
        if (text.Count(char.IsAsciiDigit) >= 8)
        {
            throw new ArgumentException("Memories must not contain account numbers or other long digit sequences.");
        }

        bool active = ToolTiers.Permits(context.ToolPermission, ToolTiers.Write);
        AgentMemory memory = repository.AddMemory(context.ConversationId, text, active ? AgentMemory.Active : AgentMemory.Proposed);
        return Task.FromResult(AgentToolOutput.Of(new { memory.MemoryId, memory.Status }));
    }
}
