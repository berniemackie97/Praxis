using Praxis.Application.Knowledge;
using Praxis.Domain.Knowledge;
using Praxis.Domain.Originals;
using Praxis.Infrastructure.Persistence;
using Xunit;

namespace Praxis.Infrastructure.Tests.Knowledge;

public sealed partial class GraphProjectionTests
{
    [Fact]
    public async Task WorkItemReferencesAreNotProjectsUnlessTheOwnerSaysSo()
    {
        using SqliteCatalog catalog = Open();
        types["AUTH-001B"] = "project";
        types["OCC #280"] = "project";
        CapturedOriginal source = await Enrich(catalog, "tickets.md", "Client ships AUTH-001B and OCC #280.");
        await Accept(catalog, source);

        EntitySummary ticket = Single(catalog, "AUTH-001B");
        EntitySummary pullRequest = Single(catalog, "OCC #280");
        EntitySummary product = Single(catalog, "Client");
        Assert.Equal("other", ticket.EntityType);
        Assert.Equal("other", pullRequest.EntityType);
        Assert.Equal("project", product.EntityType);

        GraphPage seeds = catalog.Graph.Seeds(null, new GraphQuery(EntityType: "project"));
        Assert.Equal([product.EntityId], seeds.Nodes.Select(node => node.EntityId));
        Assert.Equal("other", catalog.Graph.Seeds("AUTH-001B", new GraphQuery()).Nodes.Single().EntityType);

        Assert.True(catalog.Knowledge.SetType(ticket.EntityId, "project"));
        Assert.Equal("project", Single(catalog, "AUTH-001B").EntityType);
        Assert.Equal("project", catalog.Graph.Seeds("AUTH-001B", new GraphQuery()).Nodes.Single().EntityType);
    }

    [Fact]
    public async Task RuntimeGlueNeverAnchorsARegionAndItsLinksPageLast()
    {
        using SqliteCatalog catalog = Open();
        string[] callers = ["NodeOne", "NodeTwo", "NodeThree", "NodeFour", "NodeFive", "NodeSix"];
        types["__EH_prolog"] = "function";
        CapturedOriginal source = await Enrich(catalog, "decompiled.md", $"Client {string.Join(' ', callers)} __EH_prolog.");
        await Accept(catalog, source);
        Guid glue = Single(catalog, "__EH_prolog").EntityId;
        Guid client = Single(catalog, "Client").EntityId;
        foreach (string caller in callers)
        {
            catalog.Knowledge.Relate(Single(catalog, caller).EntityId, "calls", glue);
        }
        catalog.Knowledge.Relate(client, "references", Single(catalog, "NodeOne").EntityId);
        catalog.Knowledge.Relate(client, "references", Single(catalog, "NodeTwo").EntityId);

        GraphQuery query = new(EntityType: "concept", Limit: 1);
        List<GraphEdge> ordered = [];
        string? cursor = null;
        do
        {
            GraphPage page = catalog.Graph.Region(query with { Cursor = cursor });
            ordered.AddRange(page.Edges);
            cursor = page.NextCursor;
        } while (cursor is not null);

        bool IsGlue(GraphEdge edge) => edge.SubjectId == glue || edge.ObjectId == glue;
        Assert.Equal(8, ordered.Count);
        Assert.Equal(6, ordered.Count(IsGlue));
        Assert.False(IsGlue(ordered[0]));
        Assert.False(IsGlue(ordered[1]));
        Assert.All(ordered.Skip(2), edge => Assert.True(IsGlue(edge)));
    }
}
