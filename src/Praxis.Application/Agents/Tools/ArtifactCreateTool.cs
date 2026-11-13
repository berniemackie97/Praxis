using System.Text;
using System.Text.Json;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Tools;

/// <summary>
/// Saves an agent-authored document (budget, report, plan) as a captured Markdown source in the
/// owner's Praxis Artifacts folder, with a sources section linking its evidence. Requires allow_edits.
/// </summary>
public sealed class ArtifactCreateTool(IArtifactWriter writer, IAgentConversationRepository repository, TimeProvider clock) : IAgentTool
{
    public AgentToolDefinition Definition { get; } = new("artifact_create",
        "Save a document you wrote (budget, plan, report, summary) into the owner's Praxis library as editable Markdown. Include tables in Markdown. List the sourceIds you relied on in 'evidence' so the document links its sources. Returns artifactId for artifact_update. Requires the owner to allow edits.",
        ToolArguments.Schema("""{"type":"object","properties":{"title":{"type":"string"},"markdown":{"type":"string"},"evidence":{"type":"array","maxItems":50,"items":{"type":"string"}}},"required":["title","markdown"]}"""),
        ToolTiers.Write);

    public async Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        ToolArguments args = new(arguments);
        Guid artifactId = Guid.NewGuid();
        string title = args.RequireText("title", 80);
        string body = Compose(args.RequireText("markdown", 60_000), args.Strings("evidence", 50, 36));
        var (path, original) = await writer.WriteAsync(artifactId, title, body, cancellationToken);
        repository.SaveArtifact(new AgentArtifact(artifactId, title, original.SourceId, original.Revision, path, context.ConversationId, clock.GetUtcNow()));
        return AgentToolOutput.Of(new { artifactId, original.SourceId, original.Revision, path = Path.GetFileName(path) });
    }

    internal static string Compose(string markdown, IReadOnlyList<string> evidence)
    {
        if (evidence.Count == 0)
        {
            return markdown;
        }

        StringBuilder text = new(markdown.TrimEnd());
        text.Append("\n\n---\n**Sources**\n");
        foreach (string source in evidence.Distinct(StringComparer.Ordinal))
        {
            text.Append("- praxis://source/").Append(Guid.TryParse(source, out Guid id) ? id.ToString("D") : throw new ArgumentException("'evidence' items must be sourceIds.")).Append('\n');
        }

        return text.ToString();
    }
}
