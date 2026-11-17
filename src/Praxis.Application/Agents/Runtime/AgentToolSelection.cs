using System.Text.Json;
using Praxis.Application.Agents.Tools;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Runtime;

/// <summary>Retains common and used tools after an initial full catalog, with explicit restoration on demand.</summary>
internal sealed class AgentToolSelection(IReadOnlyList<AgentToolDefinition> permitted)
{
    private static readonly HashSet<string> Common = new(StringComparer.Ordinal)
    {
        "knowledge_search", "knowledge_open", "skill_search", "skill_open",
        "data_describe", "data_query", "compute_evaluate", "media_find", ToolExpandTool.ToolName
    };

    private readonly HashSet<string> used = new(StringComparer.Ordinal);
    private bool narrowed;
    private bool expandNext;

    public IReadOnlyList<AgentToolDefinition> ForTurn(bool evidenceOnly)
    {
        if (evidenceOnly)
        {
            return [];
        }

        if (!narrowed || expandNext)
        {
            expandNext = false;
            return permitted;
        }

        return permitted.Where(tool => Common.Contains(tool.Name) || used.Contains(tool.Name)).ToArray();
    }

    public void Observe(AgentToolCall call, string receipt)
    {
        using JsonDocument json = JsonDocument.Parse(receipt);
        if (!json.RootElement.TryGetProperty("result", out JsonElement result) ||
            result.ValueKind != JsonValueKind.Object || result.TryGetProperty("error", out _))
        {
            return;
        }

        if (call.Name == ToolExpandTool.ToolName)
        {
            if (result.TryGetProperty("expanded", out JsonElement expanded) && expanded.ValueKind == JsonValueKind.True)
            {
                expandNext = true;
            }

            return;
        }

        if (permitted.Any(tool => tool.Name == call.Name))
        {
            used.Add(call.Name);
            narrowed = true;
        }
    }
}
