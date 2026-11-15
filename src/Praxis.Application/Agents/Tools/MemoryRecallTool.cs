using System.Text.Json;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Tools;

/// <summary>The owner's accepted preferences and standing facts, optionally filtered by words.</summary>
public sealed class MemoryRecallTool(IAgentConversationRepository repository) : IAgentTool
{
    public const string ToolName = "memory_recall";
    public AgentToolDefinition Definition { get; } = new(ToolName,
        "Recall the owner's accepted preferences and standing facts (e.g. pay schedule, preferred formats, recurring obligations). Call at the start of planning tasks.",
        ToolArguments.Schema("""{"type":"object","properties":{"query":{"type":"string"},"limit":{"type":"integer","minimum":1,"maximum":50}}}"""), ToolTiers.Read);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        if (!context.BackendIsLocal)
        {
            // Statements stored as memories have no per-source remote disclosure decision.
            return Task.FromResult(AgentToolOutput.Error("permission_denied", "Saved preferences are available only to local models."));
        }

        ToolArguments args = new(arguments);
        return Task.FromResult(AgentToolOutput.Of(new
        {
            memories = repository.ReadMemories(AgentMemory.Active, args.Text("query", 128), args.Integer("limit", 20, 1, 50))
                .Select(memory => new { memory.MemoryId, memory.Text, created = memory.CreatedUtc })
        }));
    }
}
