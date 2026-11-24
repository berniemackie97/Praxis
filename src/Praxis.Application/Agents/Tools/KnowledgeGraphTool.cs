using System.Text.Json;
using Praxis.Application.Knowledge;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Tools;

/// <summary>Bounded reads of the same current graph projection used by the desktop.</summary>
public sealed class KnowledgeGraphTool(IGraphReader graph, IKnowledgeRepository knowledge) : IAgentTool
{
    public const string ToolName = "knowledge_graph";

    public AgentToolDefinition Definition { get; } = new(ToolName,
        "Inspect the Brain's existing regions, source and entity counts, pending understanding and connected entities. " +
        "Use regionId to page through a region's members; cursor continues that page. " +
        "Remote chats must supply an authorized sourceId. Region counts are navigation, not evidence of a fact. Open entities and their source passages before proposing changes.",
        ToolArguments.Schema("""
            {"type":"object","properties":{"sourceId":{"type":"string"},"regionId":{"type":"string"},
            "cursor":{"type":"string"},"offset":{"type":"integer","minimum":0,"maximum":200},
            "limit":{"type":"integer","minimum":1,"maximum":20}},"additionalProperties":false}
            """), ToolTiers.Read);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        ToolArguments args = new(arguments);
        Guid? source = args.Guid("sourceId");
        if ((!context.BackendIsLocal && source is null) || source is { } id && !context.MayDisclose(id))
        {
            return Task.FromResult(AgentToolOutput.Error("evidence_withheld", "Select an authorized sourceId to inspect its graph in this chat."));
        }

        int limit = args.Integer("limit", 8, 1, 20);
        string? region = args.Text("regionId", 256);
        string? cursor = args.Text("cursor", 4_096);
        GraphQuery query = new(SourceId: source, Limit: limit, Cursor: cursor);
        if (region is not null)
        {
            GraphPage page = graph.RegionMembers(region, query, cancellationToken);
            GraphNode[] nodes = page.Nodes.Where(node => KnowledgeDisclosure.Visible(knowledge, node.EntityId, context)).ToArray();
            HashSet<Guid> visible = nodes.Select(node => node.EntityId).ToHashSet();
            return Task.FromResult(AgentToolOutput.Of(new
            {
                page.Outcome,
                nodes = nodes.Select(node => new { node.EntityId, node.Label, node.EntityType, node.Mentions, node.Sources, node.TypeConflict }),
                relations = page.Edges.Where(edge => visible.Contains(edge.SubjectId) && visible.Contains(edge.ObjectId))
                    .Select(edge => new { edge.RelationId, edge.SubjectId, edge.ObjectId, edge.Predicate, edge.EvidenceStatus }),
                page.NextCursor
            }));
        }

        if (cursor is not null)
        {
            throw new ArgumentException("A graph cursor requires regionId.");
        }
        GraphRegionMap map = graph.KnowledgeRegions(query, cancellationToken);
        int offset = args.Integer("offset", 0, 0, 200);
        GraphKnowledgeRegion[] regions = map.Regions.Skip(offset).Take(limit).ToArray();
        HashSet<string> regionIds = regions.Select(item => item.RegionId).ToHashSet(StringComparer.Ordinal);
        return Task.FromResult(AgentToolOutput.Of(new
        {
            map.Revision,
            map.Entities,
            map.UnplacedEntities,
            regions = regions.Select(item => new
            {
                item.RegionId,
                item.ParentId,
                label = !context.BackendIsLocal && item.Kind is "group" or "theme" ? null : item.Label,
                item.Kind,
                item.Entities,
                item.Sources,
                item.PendingSources,
                item.Relations,
                hubs = item.Hubs.Where(node => KnowledgeDisclosure.Visible(knowledge, node.EntityId, context)).Take(3)
                    .Select(node => new { node.EntityId, node.Label, node.EntityType })
            }),
            links = map.Links.Where(link => regionIds.Contains(link.FromRegionId) && regionIds.Contains(link.ToRegionId)),
            nextOffset = offset + regions.Length < map.Regions.Count ? offset + regions.Length : (int?)null
        }));
    }
}
