using System.Text.Json;
using Praxis.Application.Agents.Tools;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Skills;

public sealed class SkillSearchTool(SkillLibrary library) : IAgentTool
{
    public const string ToolName = "skill_search";

    public AgentToolDefinition Definition { get; } = new(ToolName,
        "Find skill names and descriptions matching a workflow. Omit query to browse; pass nextOffset to see more. Use skill_open to load a procedure.",
        ToolArguments.Schema("""{"type":"object","properties":{"query":{"type":"string"},"offset":{"type":"integer"}}}"""), ToolTiers.Read);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const int pageSize = 8;
        ToolArguments args = new(arguments);
        string query = args.Text("query", 300) ?? "";
        int offset = args.Integer("offset", 0, 0, 500);
        IReadOnlyList<SkillDescriptor> matches = library.Search(query, context.BackendIsLocal, pageSize + 1, offset);
        return Task.FromResult(AgentToolOutput.Of(new
        {
            skills = matches.Take(pageSize).Select(item => new { item.Name, description = item.Description[..Math.Min(300, item.Description.Length)] }),
            nextOffset = matches.Count > pageSize ? (int?)(offset + pageSize) : null
        }));
    }
}
