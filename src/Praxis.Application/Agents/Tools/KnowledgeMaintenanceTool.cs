using System.Text.Json;
using Praxis.Application.Knowledge;
using Praxis.Domain.Agents;
using Praxis.Domain.Knowledge;

namespace Praxis.Application.Agents.Tools;

/// <summary>Current, fully disclosable candidates from the existing reconsolidation review queue.</summary>
public sealed class KnowledgeMaintenanceTool(IKnowledgeReconsolidation revisions, IKnowledgeRepository knowledge) : IAgentTool
{
    public const string ToolName = "knowledge_maintenance";
    public AgentToolDefinition Definition { get; } = new(ToolName,
        "Read up to three existing reconsolidation suggestions worth investigating. Does not apply or accept changes. Open each entity and its current source passages before proposing a repair.",
        ToolArguments.Schema("""{"type":"object","properties":{},"additionalProperties":false}"""), ToolTiers.Read);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        List<object> candidates = [];
        HashSet<Guid> seen = [];
        foreach (KnowledgeRevision revision in revisions.Revisions(RevisionVocabulary.Suggested, 50))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(revision.EntityId) || knowledge.GetEntity(revision.EntityId) is not { } entity ||
                entity.Mentions.Count == 0 || entity.Mentions.Any(mention => !context.MayDisclose(mention.SourceId)))
            {
                continue;
            }

            // Revision text can name a hidden merge target or neighbor. Re-read current evidence instead.
            candidates.Add(new
            {
                revision.RevisionId,
                entityId = revision.EntityId,
                name = entity.Summary.Name,
                type = entity.Summary.EntityType,
                field = revision.Field
            });
            if (candidates.Count == 3)
            {
                break;
            }
        }

        return Task.FromResult(AgentToolOutput.Of(new
        {
            candidates,
            proposalOnly = true,
            note = candidates.Count == 0 ? "No authorized pending suggestions in this bounded queue page." : "Investigate current evidence; suggestions are not facts."
        }));
    }
}
