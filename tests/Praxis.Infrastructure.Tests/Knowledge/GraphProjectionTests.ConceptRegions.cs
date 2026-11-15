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
    public async Task ConceptRegionsUseRealProjectIdentitiesAndSeparateMembershipFromAssociation()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal source = await Enrich(catalog, "project.md", "Client NodeOne NodeTwo.");
        await Accept(catalog, source);

        Guid project = Single(catalog, "Client").EntityId;
        Guid component = Single(catalog, "NodeOne").EntityId;
        Guid related = Single(catalog, "NodeTwo").EntityId;

        GraphConceptRegion empty = Assert.Single(catalog.Graph.ConceptRegions(new GraphQuery()).Regions);
        Assert.Equal(project, empty.RegionId);
        Assert.Equal("Client", empty.Label);
        Assert.Equal(0, empty.RelationCount);
        Assert.Equal(0, empty.RelatedEntityCount);
        Assert.Equal(0, empty.ContainedEntityCount);

        catalog.Knowledge.Relate(project, "contains", component);
        catalog.Knowledge.Relate(component, "part_of", project);
        catalog.Knowledge.Relate(project, "references", related);

        GraphConceptPage page = catalog.Graph.ConceptRegions(new GraphQuery());
        Assert.Equal(1, page.RegionCount);
        Assert.Null(page.NextCursor);
        GraphConceptRegion region = Assert.Single(page.Regions);
        Assert.Equal(project, region.RegionId);
        Assert.Equal(3, region.RelationCount);
        Assert.Equal(2, region.RelatedEntityCount);
        Assert.Equal(1, region.ContainedEntityCount);

        Assert.True(catalog.Knowledge.Rename(project, "Client Workspace"));
        GraphConceptRegion renamed = Assert.Single(catalog.Graph.ConceptRegions(new GraphQuery()).Regions);
        Assert.Equal(project, renamed.RegionId);
        Assert.Equal("Client Workspace", renamed.Label);
    }

    [Fact]
    public async Task ConceptRegionsEvolveWithAcceptedSourcesAndDisappearOnRevocation()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal first = await Enrich(catalog, "first.md", "Client NodeOne.");
        await Accept(catalog, first);
        Guid firstProject = Single(catalog, "Client").EntityId;

        CapturedOriginal second = await Enrich(catalog, "second.md", "C3Reader PHY.");
        Assert.Equal(firstProject, Assert.Single(catalog.Graph.ConceptRegions(new GraphQuery()).Regions).RegionId);
        Assert.Throws<ArgumentException>(() => catalog.Graph.ConceptRegions(
            new GraphQuery(IncludeSuggestions: true)));

        await Accept(catalog, second);
        Guid secondProject = Single(catalog, "C3Reader").EntityId;
        GraphConceptPage all = catalog.Graph.ConceptRegions(new GraphQuery());
        Assert.Equal(2, all.RegionCount);
        Assert.Equal(new[] { firstProject, secondProject }.Order(),
            all.Regions.Select(r => r.RegionId).Order());

        GraphConceptPage firstScope = catalog.Graph.ConceptRegions(new GraphQuery(SourceId: first.SourceId));
        Assert.Equal(firstProject, Assert.Single(firstScope.Regions).RegionId);
        GraphConceptPage secondScope = catalog.Graph.ConceptRegions(new GraphQuery(SourceId: second.SourceId));
        Assert.Equal(secondProject, Assert.Single(secondScope.Regions).RegionId);
        Assert.DoesNotContain(secondScope.Regions, region => region.Label == "Client");

        Guid collection = catalog.Collections.Create("First project").CollectionId;
        Assert.True(catalog.Collections.AddSource(collection, first.SourceId));
        Assert.Equal(firstProject, Assert.Single(
            catalog.Graph.ConceptRegions(new GraphQuery(CollectionId: collection)).Regions).RegionId);
        Assert.True(catalog.Collections.Archive(collection));
        Assert.Empty(catalog.Graph.ConceptRegions(new GraphQuery(CollectionId: collection)).Regions);

        Assert.True(catalog.Sources.Revoke(second.SourceId));
        Assert.Equal(firstProject, Assert.Single(catalog.Graph.ConceptRegions(new GraphQuery()).Regions).RegionId);
        Assert.Empty(catalog.Graph.ConceptRegions(new GraphQuery(SourceId: second.SourceId)).Regions);
    }

    [Fact]
    public async Task ConceptRegionsNeverTurnPendingOrRejectedMembershipIntoOwnedTerritory()
    {
        using SqliteCatalog catalog = Open();
        relations.Add(("Client", "contains", "NodeOne"));
        CapturedOriginal source = await Enrich(catalog, "suggestion.md", "Client contains NodeOne.");

        Assert.Empty(catalog.Graph.ConceptRegions(new GraphQuery()).Regions);
        await Accept(catalog, source);
        GraphConceptRegion confirmed = Assert.Single(catalog.Graph.ConceptRegions(new GraphQuery()).Regions);
        Assert.Equal(1, confirmed.ContainedEntityCount);
        Assert.Equal(1, confirmed.RelationCount);

        Guid proposal = catalog.Proposals.List(source.SourceId)
            .Single(item => item.Kind == "relation").ProposalId;
        Assert.True(catalog.Proposals.Review(proposal, "rejected"));
        GraphConceptRegion rejected = Assert.Single(catalog.Graph.ConceptRegions(new GraphQuery()).Regions);
        Assert.Equal(0, rejected.ContainedEntityCount);
        Assert.Equal(0, rejected.RelationCount);
        Assert.True(catalog.Sources.Revoke(source.SourceId));
        Assert.Empty(catalog.Graph.ConceptRegions(new GraphQuery()).Regions);
    }

    [Fact]
    public async Task ConceptRegionsHaveScopedDesktopContractAndRevisionBoundCursors()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal first = await Enrich(catalog, "first.md", "Client NodeOne.");
        await Accept(catalog, first);
        CapturedOriginal second = await Enrich(catalog, "second.md", "C3Reader PHY.");
        await Accept(catalog, second);

        GraphQuery query = new(Limit: 1);
        GraphConceptPage firstPage = catalog.Graph.ConceptRegions(query);
        Assert.Equal(2, firstPage.RegionCount);
        Assert.Single(firstPage.Regions);
        Assert.NotNull(firstPage.NextCursor);
        GraphConceptPage next = catalog.Graph.ConceptRegions(query with { Cursor = firstPage.NextCursor });
        Assert.Single(next.Regions);
        Assert.Null(next.NextCursor);
        Assert.NotEqual(firstPage.Regions[0].RegionId, next.Regions[0].RegionId);
        Assert.Throws<ArgumentException>(() => catalog.Graph.ConceptRegions(
            query with { Cursor = firstPage.NextCursor, SourceId = first.SourceId }));

        DesktopProtocol desktop = TestDesktop.Protocol(catalog);
        string request = JsonSerializer.Serialize(new
        {
            requestId = "concepts",
            action = "graphConceptRegions",
            sourceId = first.SourceId,
            limit = 1
        });
        using JsonDocument result = JsonDocument.Parse(await desktop.HandleAsync(request));
        Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
        JsonElement data = result.RootElement.GetProperty("data");
        Assert.Equal(1, data.GetProperty("regionCount").GetInt32());
        Assert.Equal(Single(catalog, "Client").EntityId.ToString("D"),
            data.GetProperty("regions")[0].GetProperty("regionId").GetString());
        Assert.Equal(0, data.GetProperty("regions")[0].GetProperty("containedEntityCount").GetInt32());

        Assert.True(catalog.Knowledge.Rename(Single(catalog, "Client").EntityId, "Client Project"));
        Assert.Throws<InvalidOperationException>(() => catalog.Graph.ConceptRegions(
            query with { Cursor = firstPage.NextCursor }));
        Assert.Throws<ArgumentException>(() => catalog.Graph.ConceptRegions(
            new GraphQuery(EntityType: "project")));
        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => catalog.Graph.ConceptRegions(
            new GraphQuery(), canceled.Token));

        Assert.True(catalog.Sources.Revoke(first.SourceId));
        using JsonDocument revoked = JsonDocument.Parse(await desktop.HandleAsync(request));
        Assert.True(revoked.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(0, revoked.RootElement.GetProperty("data").GetProperty("regionCount").GetInt32());
        Assert.Empty(revoked.RootElement.GetProperty("data").GetProperty("regions").EnumerateArray());
    }
}
