using System.Text.Json;
using Praxis.Application.Agents.Memory;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Tools;

/// <summary>The owner's accepted preferences and standing facts, optionally filtered by words.</summary>
public sealed class MemoryRecallTool(IAgentConversationRepository repository, IChatMemoryStore? chats = null) : IAgentTool
{
    public const string ToolName = "memory_recall";
    public AgentToolDefinition Definition { get; } = new(ToolName,
        "Recall accepted preferences and standing facts. With a short distinctive query, also find private recaps of earlier chats. Recaps are generated navigation aids, not verified facts. Available only to local models.",
        ToolArguments.Schema("""{"type":"object","properties":{"query":{"type":"string"},"limit":{"type":"integer","minimum":1,"maximum":50}}}"""), ToolTiers.Read);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        if (!context.BackendIsLocal)
        {
            // Statements stored as memories have no per-source remote disclosure decision.
            return Task.FromResult(AgentToolOutput.Error("permission_denied", "Saved preferences are available only to local models."));
        }

        ToolArguments args = new(arguments);
        string? query = args.Text("query", 128);
        return Task.FromResult(AgentToolOutput.Of(new
        {
            memories = repository.ReadMemories(AgentMemory.Active, query, args.Integer("limit", 20, 1, 50))
                .Select(memory => new { memory.MemoryId, memory.Text, created = memory.CreatedUtc }),
            ownerCorrections = query is null ? [] : repository.FindOwnerCorrections(query, 3),
            conversations = query is null ? [] : chats?.Search(query, context.ConversationId, 3) ?? [],
            recapBasis = "Generated summaries of retained dialogue; verify claims against original evidence."
        }));
    }
}
