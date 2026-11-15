using System.Text.Json;
using Praxis.Application.Agents.Tools;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Skills;

public sealed class SkillProposeTool(SkillLibrary library) : IAgentTool
{
    public const string ToolName = "skill_propose";

    public AgentToolDefinition Definition { get; } = new(ToolName,
        "Propose a reusable text-only SKILL.md after owner teaching or a verified successful run. Include a rationale, inputs, steps, checks and a stopping condition. Remove private examples, credentials and data. For revisions, pass baseRevisionId from skill_open. The owner must review it in Skills.",
        ToolArguments.Schema("""
            {"type":"object","properties":{"markdown":{"type":"string"},"rationale":{"type":"string"},"baseRevisionId":{"type":"string"}},
            "required":["markdown","rationale"]}
            """), ToolTiers.Propose, LocalOnly: true, AuditArguments: false);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!context.BackendIsLocal)
        {
            return Task.FromResult(AgentToolOutput.Error("local_only", "Use a local model to teach Praxis a skill."));
        }

        ToolArguments args = new(arguments);
        SkillRevisionSummary draft = library.Propose(args.RequireText("markdown", SkillDocument.MaximumCharacters), context.ConversationId,
            args.RequireText("rationale", 1_000), args.Guid("baseRevisionId"));
        return Task.FromResult(AgentToolOutput.Of(new { draft.RevisionId, draft.Name, status = "awaiting_owner_review" }));
    }
}
