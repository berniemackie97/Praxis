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
    public async Task RegionsFollowTheOwnersFoldersAndBindFoldersThatShareKnowledge()
    {
        using SqliteCatalog catalog = Open();
        string inbox = Path.Combine(workspace, "Inbox");
        types["Engine"] = "project";
        types["Renderer"] = "concept";
        types["Network"] = "concept";
        types["Ledger"] = "concept";
        string shared = "Engine NodeOne NodeTwo NodeThree";
        List<CapturedOriginal> sources =
        [
            await Enrich(catalog, "notes/Rendering/a.md", $"{shared} Renderer.", inbox),
            await Enrich(catalog, "notes/Network/b.md", $"{shared} Network.", inbox),
            await Enrich(catalog, "EngineClient/src/Engine.Net/c.md", $"{shared}.", inbox),
            await Enrich(catalog, "EngineClient/.github/ci.md", "Engine.", inbox),
            await Enrich(catalog, "business/01_Banking/ledger.md", "Ledger.", inbox),
            await Enrich(catalog, "cheatsheet.md", "Alex Rivera.", inbox)
        ];
        foreach (CapturedOriginal source in sources)
        {
            await Accept(catalog, source);
        }

        catalog.Knowledge.Relate(Single(catalog, "Renderer").EntityId, "references", Single(catalog, "Network").EntityId);
        GraphRegionMap map = catalog.Graph.KnowledgeRegions(new GraphQuery());
        ILookup<string?, GraphKnowledgeRegion> byParent = map.Regions.ToLookup(region => region.ParentId);
        GraphKnowledgeRegion group = Assert.Single(byParent[null], region => region.Kind == "group");
        Assert.Equal("Engine", group.Label);
        Assert.Equal(["EngineClient", "notes"], byParent[group.RegionId].Select(region => region.Label).Order(StringComparer.Ordinal));
        GraphKnowledgeRegion notes = byParent[group.RegionId].Single(region => region.Label == "notes");
        Assert.Equal(["Network", "Rendering"], byParent[notes.RegionId].Select(region => region.Label).Order(StringComparer.Ordinal));
        GraphKnowledgeRegion client = byParent[group.RegionId].Single(region => region.Label == "EngineClient");
        Assert.Equal(["Engine.Net"], byParent[client.RegionId].Select(region => region.Label));
        Assert.Contains(byParent[null], region => region.Label == "business" && region.Kind == "folder");
        Assert.Contains(byParent[null], region => region.Kind == "loose" && region.Label == "Loose files in Inbox");
        Assert.Contains(map.Regions, region => region.Label == "Banking");
        Assert.Equal(0, map.UnplacedEntities);
        Assert.Equal(1, notes.Relations);
        Assert.DoesNotContain(map.Regions, region => region.Label.StartsWith('.'));

        GraphPage members = catalog.Graph.RegionMembers(notes.RegionId, new GraphQuery(Limit: 2));
        Assert.Equal(2, members.Nodes.Count);
        Assert.NotNull(members.NextCursor);
        GraphPage rest = catalog.Graph.RegionMembers(notes.RegionId, new GraphQuery(Limit: 2, Cursor: members.NextCursor));
        Assert.NotEmpty(rest.Nodes);
        Assert.Empty(members.Nodes.Select(node => node.EntityId).Intersect(rest.Nodes.Select(node => node.EntityId)));
        Assert.Equal("unavailable", catalog.Graph.RegionMembers(new string('0', 32), new GraphQuery()).Outcome);

        DesktopProtocol desktop = TestDesktop.Protocol(catalog);
        using JsonDocument reply = JsonDocument.Parse(await desktop.HandleAsync(
            """{"requestId":"regions","action":"graphKnowledgeRegions"}"""));
        Assert.True(reply.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(map.Regions.Count, reply.RootElement.GetProperty("data").GetProperty("regions").GetArrayLength());
        using JsonDocument invalid = JsonDocument.Parse(await desktop.HandleAsync(
            """{"requestId":"bad","action":"graphRegionMembers","regionId":"not-hex"}"""));
        Assert.False(invalid.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task RevokingASourceShrinksItsRegionAndScopeLimitsWhatRegionsShow()
    {
        using SqliteCatalog catalog = Open();
        string inbox = Path.Combine(workspace, "Inbox");
        CapturedOriginal keep = await Enrich(catalog, "alpha/one.md", "Client NodeOne.", inbox);
        CapturedOriginal drop = await Enrich(catalog, "beta/two.md", "Alex Rivera.", inbox);
        await Accept(catalog, keep);
        await Accept(catalog, drop);
        Assert.Equal(["alpha", "beta"], catalog.Graph.KnowledgeRegions(new GraphQuery()).Regions
            .Where(region => region.ParentId is null).Select(region => region.Label).Order(StringComparer.Ordinal));

        Assert.Equal(["alpha"], catalog.Graph.KnowledgeRegions(new GraphQuery(SourceId: keep.SourceId)).Regions
            .Where(region => region.Entities > 0).Select(region => region.Label));
        Assert.True(catalog.Sources.Revoke(drop.SourceId));
        GraphRegionMap after = catalog.Graph.KnowledgeRegions(new GraphQuery());
        Assert.DoesNotContain(after.Regions, region => region.Label == "beta");
        Assert.DoesNotContain(after.Regions.SelectMany(region => region.Hubs), node => node.Label == "Alex Rivera");
    }
}
