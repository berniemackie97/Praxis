using System.Text.Json;
using Praxis.Application.Knowledge;
using Praxis.Domain.Agents;
using Praxis.Domain.Knowledge;

namespace Praxis.Application.Agents.Tools;

/// <summary>A profile of a person, organization, project, place or other entity, with cited mentions.</summary>
public sealed class KnowledgeEntityTool(IKnowledgeRepository knowledge) : IAgentTool
{
    public AgentToolDefinition Definition { get; } = new("knowledge_entity",
        "Look up an entity (person, organization, project, product, place, ...) by entityId or name. Returns type and how it was decided, aliases, typed relations, co-occurring entities and cited mentions.",
        ToolArguments.Schema("""{"type":"object","properties":{"entityId":{"type":"string"},"name":{"type":"string"},"mentions":{"type":"integer","minimum":0,"maximum":20}}}"""),
        ToolTiers.Read);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        ToolArguments args = new(arguments);
        Guid? id = args.Guid("entityId");
        if (id is null)
        {
            string name = args.Text("name", 128) ?? throw new ArgumentException("Provide entityId or name.");
            IReadOnlyList<EntitySummary> matches = knowledge.ListEntities(null, name, 10);
            if (matches.Count != 1)
            {
                return Task.FromResult(AgentToolOutput.Of(new
                {
                    matches = matches.Select(entity => new { entity.EntityId, entity.Name, entity.EntityType, entity.Mentions, entity.Sources }),
                    hint = matches.Count == 0 ? "No entity by that name; try knowledge_search." : "Several entities match; call again with entityId."
                }));
            }

            id = matches[0].EntityId;
        }

        EntityDetail detail = knowledge.GetEntity(id.Value) ?? throw new ArgumentException("No entity with that entityId.");
        EntityMention[] visible = detail.Mentions.Where(mention => context.MayDisclose(mention.SourceId)).ToArray();
        EntityMention[] shown = visible.Take(args.Integer("mentions", 8, 0, 20)).ToArray();
        int withheld = detail.Mentions.Count - visible.Length;
        return Task.FromResult(AgentToolOutput.Of(new
        {
            detail.Summary.EntityId,
            detail.Summary.Name,
            type = detail.Summary.EntityType,
            typeOrigin = detail.Summary.TypeOrigin,
            detail.Summary.TypeConflict,
            detail.Aliases,
            relations = detail.Relations,
            related = detail.Related.Take(15),
            mentions = shown.Select(mention => new { mention.SourceId, mention.ItemId, mention.Revision, source = mention.SourceName, quote = mention.Evidence, mention.Verdict })
        },
        shown.Select(mention => new AgentCitation(mention.SourceId, mention.ItemId, mention.Revision, $"chars:{mention.EvidenceOffset}-{mention.EvidenceOffset + mention.Evidence.Length}", mention.SourceName)).ToArray(),
        withheld > 0 ? [new WithheldNotice(withheld, WithheldNotice.DisclosurePolicy)] : null));
    }
}
