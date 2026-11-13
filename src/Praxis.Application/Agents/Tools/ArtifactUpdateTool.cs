using System.Text.Json;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Tools;

/// <summary>Writes a new revision of an existing artifact; earlier revisions stay recoverable. Requires allow_edits.</summary>
public sealed class ArtifactUpdateTool(IArtifactWriter writer, IAgentConversationRepository repository, TimeProvider clock) : IAgentTool
{
    public AgentToolDefinition Definition { get; } = new("artifact_update",
        "Replace the content of a document you created with artifact_create (a new revision is recorded; earlier ones remain). Requires the owner to allow edits.",
        ToolArguments.Schema("""{"type":"object","properties":{"artifactId":{"type":"string"},"title":{"type":"string"},"markdown":{"type":"string"},"evidence":{"type":"array","maxItems":50,"items":{"type":"string"}}},"required":["artifactId","markdown"]}"""),
        ToolTiers.Write);

    public async Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        ToolArguments args = new(arguments);
        AgentArtifact artifact = repository.ReadArtifact(args.RequireGuid("artifactId")) ?? throw new ArgumentException("No artifact with that artifactId.");
        string title = args.Text("title", 80) ?? artifact.Title;
        var (path, original) = await writer.WriteAsync(artifact.ArtifactId, title,
            ArtifactCreateTool.Compose(args.RequireText("markdown", 60_000), args.Strings("evidence", 50, 36)), cancellationToken);
        repository.SaveArtifact(artifact with { Title = title, SourceId = original.SourceId, Revision = original.Revision, Path = path, UpdatedUtc = clock.GetUtcNow() });
        return AgentToolOutput.Of(new { artifact.ArtifactId, original.SourceId, original.Revision });
    }
}
