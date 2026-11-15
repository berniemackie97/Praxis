using Praxis.Application.Knowledge;

namespace Praxis.Application.Desktop.Actions;

/// <summary>Read-only graph exploration through the existing private desktop transport.</summary>
public sealed class GraphActions(IGraphReader graph) : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        new DesktopAction("graphOverview", (fields, token) =>
            Task.FromResult<object>(graph.Overview(Query(fields), token))),
        new DesktopAction("graphZones", (fields, token) =>
            Task.FromResult<object>(graph.Zones(Query(fields), token))),
        new DesktopAction("graphAtlas", (fields, token) =>
            Task.FromResult<object>(graph.Atlas(Query(fields), token))),
        new DesktopAction("graphRegion", (fields, token) =>
            Task.FromResult<object>(graph.Region(Query(fields), token))),
        new DesktopAction("graphSeeds", (fields, token) =>
            Task.FromResult<object>(graph.Seeds(
                fields.OptionalText("search", 120, "The graph search is invalid."),
                Query(fields), token))),
        new DesktopAction("graphNeighborhood", (fields, token) =>
            Task.FromResult<object>(graph.Neighborhood(fields.RequireGuid("entityId"), Query(fields), token))),
        new DesktopAction("graphRelation", (fields, token) =>
            Task.FromResult<object>(graph.Relation(
                fields.RequireGuid("entityId"), fields.RequireGuid("relationId"), Query(fields), token)))
    ];

    private static GraphQuery Query(DesktopRequestFields fields) => new(
        fields.OptionalGuid("collectionId"),
        fields.OptionalGuid("sourceId"),
        fields.OptionalText("entityType", 32, "The graph entity type is invalid."),
        fields.OptionalText("predicate", 32, "The graph relation predicate is invalid."),
        fields.OptionalText("evidenceStatus", 32, "The graph evidence status is invalid."),
        fields.IntegerOrDefault("limit", 40, 1, 100),
        fields.OptionalText("cursor", 256, "The graph cursor is invalid."),
        fields.OptionalBoolean("includeSuggestions") ?? false);
}
