using System.Text.Json;
using Praxis.Application.Desktop;
using Praxis.Application.Knowledge;
using Praxis.Domain.Originals;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Knowledge;

public sealed partial class GraphProjectionTests
{
    [Fact]
    public async Task RankedRegionPagesDistinctRealHubsBeforeDeepEdgesAndPreservesEveryObservation()
    {
        using SqliteCatalog catalog = Open();
        string[] names = ["NodeOne", "NodeTwo", "NodeThree", "NodeFour", "NodeFive", "NodeSix"];
        CapturedOriginal large = await Enrich(catalog, "large.md", $"Client {string.Join(' ', names)}.");
        await Accept(catalog, large);
        Guid client = Single(catalog, "Client").EntityId;
        foreach (string name in names)
        {
            catalog.Knowledge.Relate(client, "references", Single(catalog, name).EntityId);
        }

        relations.Add(("C3Reader", "parses", "PHY"));
        CapturedOriginal small = await Enrich(catalog, "small.md", "C3Reader parses PHY.");
        await Accept(catalog, small);
        Guid reader = Single(catalog, "C3Reader").EntityId;

        GraphQuery query = new(EntityType: "project", Limit: 1);
        GraphPage first = catalog.Graph.Region(query);
        Assert.Single(first.Edges);
        Assert.Contains(first.Edges, edge => edge.SubjectId == client);
        Assert.NotNull(first.NextCursor);

        GraphPage second = catalog.Graph.Region(query with { Cursor = first.NextCursor });
        Assert.Single(second.Edges);
        Assert.Contains(second.Edges, edge => edge.SubjectId == reader);
        Assert.NotNull(second.NextCursor);

        List<GraphEdge> edges = [.. first.Edges, .. second.Edges];
        string? cursor = second.NextCursor;
        while (cursor is not null)
        {
            GraphPage page = catalog.Graph.Region(query with { Cursor = cursor });
            edges.AddRange(page.Edges);
            cursor = page.NextCursor;
        }
        Assert.Equal(7, edges.Count);
        Assert.Equal(7, edges.Select(edge => edge.RelationId).Distinct().Count());
        Assert.All(edges, edge => Assert.Equal("ready",
            catalog.Graph.Relation(edge.SubjectId, edge.RelationId, new GraphQuery()).Outcome));

        Assert.Throws<ArgumentException>(() => catalog.Graph.Region(new GraphQuery()));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            catalog.Graph.Region(new GraphQuery(EntityType: "project", Limit: 101)));
        Assert.Throws<ArgumentException>(() =>
            catalog.Graph.Region(query with { Cursor = first.NextCursor, EntityType = "technology" }));
        Assert.Throws<ArgumentException>(() =>
            catalog.Graph.Region(query with { Cursor = "garbage" }));

        Assert.Equal(6, CountRegion(catalog, new GraphQuery(EntityType: "project", SourceId: large.SourceId)));
        Assert.Equal(1, CountRegion(catalog, new GraphQuery(EntityType: "project", SourceId: small.SourceId)));

        Assert.True(catalog.Sources.Revoke(small.SourceId));
        Assert.Equal(6, CountRegion(catalog, new GraphQuery(EntityType: "project")));
        Assert.Equal("empty", catalog.Graph.Region(new GraphQuery(
            EntityType: "project", SourceId: small.SourceId)).Outcome);
        Assert.Throws<InvalidOperationException>(() =>
            catalog.Graph.Region(query with { Cursor = first.NextCursor }));
    }

    [Fact]
    public async Task RankedRegionSeparatesProposedConnectionsAndEnforcesDesktopScope()
    {
        using SqliteCatalog catalog = Open();
        relations.Add(("C3Reader", "parses", "PHY"));
        CapturedOriginal source = await Enrich(catalog, "draft.md", "C3Reader parses PHY.");

        GraphQuery scoped = new(EntityType: "project", SourceId: source.SourceId, Limit: 10);
        Assert.Empty(catalog.Graph.Region(scoped).Edges);
        GraphEdge pending = Assert.Single(catalog.Graph.Region(scoped with
        { IncludeSuggestions = true }).Edges);
        Assert.Equal("suggested", pending.EvidenceStatus);
        Assert.Equal(source.SourceId, pending.Evidence!.SourceId);

        DesktopProtocol desktop = TestDesktop.Protocol(catalog);
        string request = JsonSerializer.Serialize(new
        {
            requestId = "region",
            action = "graphRegion",
            entityType = "project",
            sourceId = source.SourceId,
            includeSuggestions = true
        });
        using JsonDocument current = JsonDocument.Parse(await desktop.HandleAsync(request));
        Assert.True(current.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, current.RootElement.GetProperty("data")
            .GetProperty("edges").GetArrayLength());

        await Accept(catalog, source);
        Assert.Equal("accepted", Assert.Single(catalog.Graph.Region(scoped).Edges).EvidenceStatus);
        Assert.True(catalog.Sources.Revoke(source.SourceId));
        Assert.Empty(catalog.Graph.Region(scoped with { IncludeSuggestions = true }).Edges);
        using JsonDocument revoked = JsonDocument.Parse(await desktop.HandleAsync(request));
        Assert.True(revoked.RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(revoked.RootElement.GetProperty("data").GetProperty("nodes").EnumerateArray());
        Assert.Empty(revoked.RootElement.GetProperty("data").GetProperty("edges").EnumerateArray());
    }

    private static int CountRegion(SqliteCatalog catalog, GraphQuery query)
    {
        int count = 0;
        string? cursor = null;
        do
        {
            GraphPage page = catalog.Graph.Region(query with { Cursor = cursor });
            count += page.Edges.Count;
            cursor = page.NextCursor;
        } while (cursor is not null);
        return count;
    }
}
