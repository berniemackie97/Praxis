using System.Text.Json;
using Praxis.Application.Knowledge;
using Praxis.Domain.Agents;
using Praxis.Domain.Knowledge;

namespace Praxis.Application.Agents.Tools;

/// <summary>Walks the entity graph: neighbours of an entity, or the shortest connection between two (up to three hops).</summary>
public sealed class KnowledgeRelatedTool(IKnowledgeRepository knowledge) : IAgentTool
{
    private const int MaximumExpansions = 200;

    public AgentToolDefinition Definition { get; } = new("knowledge_related",
        "Explore connections in the knowledge graph. With entityId only: its typed relations and co-occurring entities. With entityId and toEntityId: the shortest chain of relations or co-occurrences linking them (max 3 hops).",
        ToolArguments.Schema("""{"type":"object","properties":{"entityId":{"type":"string"},"toEntityId":{"type":"string"}},"required":["entityId"]}"""),
        ToolTiers.Read);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        ToolArguments args = new(arguments);
        Guid from = args.RequireGuid("entityId");
        EntityDetail start = knowledge.GetEntity(from) ?? throw new ArgumentException("No entity with that entityId.");
        if (args.Guid("toEntityId") is not { } to)
        {
            return Task.FromResult(AgentToolOutput.Of(new
            {
                entity = start.Summary.Name,
                relations = start.Relations,
                neighbours = start.Related.Select(related => new { related.EntityId, related.Name, related.EntityType, related.SharedSegments })
            }));
        }

        Dictionary<Guid, (Guid Previous, string Link)> visited = new() { [from] = (Guid.Empty, "start") };
        Queue<(Guid Entity, int Depth)> frontier = new([(from, 0)]);
        int expansions = 0;
        while (frontier.Count > 0 && !visited.ContainsKey(to) && expansions++ < MaximumExpansions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (Guid current, int depth) = frontier.Dequeue();
            if (depth >= 3 || knowledge.GetEntity(current) is not { } detail)
            {
                continue;
            }

            IEnumerable<(Guid Id, string Link)> next = detail.Related.Select(related => (related.EntityId, $"co-occurs ({related.SharedSegments} passages)"));
            foreach ((Guid id, string link) in next)
            {
                if (visited.TryAdd(id, (current, link)))
                {
                    frontier.Enqueue((id, depth + 1));
                }
            }
        }

        if (!visited.ContainsKey(to))
        {
            return Task.FromResult(AgentToolOutput.Of(new { connected = false, searchedEntities = visited.Count }));
        }

        List<object> path = [];
        for (Guid step = to; step != from; step = visited[step].Previous)
        {
            path.Insert(0, new { entityId = step, name = knowledge.GetEntity(step)?.Summary.Name, via = visited[step].Link });
        }

        return Task.FromResult(AgentToolOutput.Of(new { connected = true, from = start.Summary.Name, path }));
    }
}
