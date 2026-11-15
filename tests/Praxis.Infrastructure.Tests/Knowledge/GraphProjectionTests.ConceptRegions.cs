using System.Text.Json;
using Praxis.Application.Desktop;
using Praxis.Application.Inference;
using Praxis.Application.Knowledge;
using Praxis.Application.Verification;
using Praxis.Domain.Originals;
using Praxis.Domain.Verification;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Knowledge;

public sealed partial class GraphProjectionTests
{
    [Fact]
    public async Task MixedCorpusUsesExplicitStructureRatherThanProjectTagsOrSourceCoOccurrence()
    {
        using SqliteCatalog catalog = Open();
        types["Harbor Toolkit"] = "project";
        types["Packet Decoder"] = "project"; // Model mistake: the source explicitly calls it a library.
        types["DecodeFrame"] = "function";
        types["Replay Compatibility"] = "concept";
        types["Atlas Sketch"] = "project";
        types["Household Planning"] = "concept";
        types["Repair Reserve"] = "concept";
        relations.Add(("Harbor Toolkit", "contains", "Packet Decoder"));
        relations.Add(("DecodeFrame", "part_of", "Packet Decoder"));
        relations.Add(("Harbor Toolkit", "references", "Replay Compatibility"));
        relations.Add(("Atlas Sketch", "references", "Replay Compatibility"));
        CapturedOriginal technical = await Enrich(catalog, "Decoder Project.md", """
            Harbor Toolkit is a project and contains the Packet Decoder library.
            DecodeFrame is a function in Packet Decoder.
            Harbor Toolkit and Atlas Sketch reference Replay Compatibility. Atlas Sketch is an independent initiative.
            """);
        await Accept(catalog, technical);
        Guid harbor = Single(catalog, "Harbor Toolkit").EntityId;
        Guid decoder = Single(catalog, "Packet Decoder").EntityId;
        Guid atlas = Single(catalog, "Atlas Sketch").EntityId;

        GraphConceptRegion onlyProject = Assert.Single(catalog.Graph.ConceptRegions(new GraphQuery()).Regions);
        Assert.Equal(harbor, onlyProject.RegionId);
        Assert.Equal(1, onlyProject.ContainedEntityCount);
        Assert.Equal(1, onlyProject.AcceptedMemberCount);
        GraphConceptRegion child = Assert.Single(catalog.Graph.ConceptRegions(new GraphQuery(), parentRegionId: harbor).Regions);
        Assert.Equal(decoder, child.RegionId);
        Assert.Equal(1, child.ContainedEntityCount);
        Assert.Empty(catalog.Graph.ConceptRegions(new GraphQuery(), parentRegionId: atlas).Regions);

        GraphClassificationAuditPage audit = catalog.Graph.ClassificationAudit(new GraphQuery());
        Assert.Equal(3, audit.ProjectTypedCount);
        GraphClassificationAuditEntry falsePositive = Assert.Single(audit.Entries, row => row.Entity.EntityId == decoder);
        Assert.Equal("inferred", falsePositive.Entity.TypeOrigin);
        Assert.Equal(1, falsePositive.AcceptedProjectMentions);
        Assert.Equal(technical.SourceId, falsePositive.Entity.Evidence!.SourceId);
        Assert.Equal("project", Single(catalog, "Packet Decoder").EntityType);

        Assert.True(catalog.Knowledge.SetType(decoder, "technology"));
        Assert.Equal(2, catalog.Graph.ClassificationAudit(new GraphQuery()).ProjectTypedCount);
        Assert.Equal(decoder, Assert.Single(catalog.Graph.ConceptRegions(new GraphQuery(), parentRegionId: harbor).Regions).RegionId);

        CapturedOriginal planning = await Enrich(catalog, "budget-notes.md", """
            Household Planning includes a Repair Reserve for unplanned home repairs.
            """);
        await Accept(catalog, planning);
        Guid household = Single(catalog, "Household Planning").EntityId;
        catalog.Knowledge.Relate(household, "contains", Single(catalog, "Repair Reserve").EntityId);

        GraphConceptPage all = catalog.Graph.ConceptRegions(new GraphQuery());
        Assert.Equal(new[] { harbor, household }.Order(), all.Regions.Select(r => r.RegionId).Order());
        Assert.Equal(1, all.Regions.Single(r => r.RegionId == household).OwnerMemberCount);
        Assert.Equal(harbor, Assert.Single(catalog.Graph.ConceptRegions(new GraphQuery(SourceId: technical.SourceId)).Regions).RegionId);
        Assert.Equal(household, Assert.Single(catalog.Graph.ConceptRegions(new GraphQuery(SourceId: planning.SourceId)).Regions).RegionId);
        Assert.Equal(0, catalog.Graph.ClassificationAudit(new GraphQuery(SourceId: planning.SourceId)).ProjectTypedCount);
        Assert.Empty(catalog.Graph.ConceptRegions(new GraphQuery(SourceId: planning.SourceId), parentRegionId: harbor).Regions);

        Guid collection = catalog.Collections.Create("Technical notes").CollectionId;
        Assert.True(catalog.Collections.AddSource(collection, technical.SourceId));
        Assert.Equal(harbor, Assert.Single(catalog.Graph.ConceptRegions(new GraphQuery(CollectionId: collection)).Regions).RegionId);
        Assert.True(catalog.Collections.Archive(collection));
        Assert.Empty(catalog.Graph.ConceptRegions(new GraphQuery(CollectionId: collection)).Regions);

        Assert.True(catalog.Sources.Revoke(technical.SourceId));
        Assert.Equal(household, Assert.Single(catalog.Graph.ConceptRegions(new GraphQuery()).Regions).RegionId);
        Assert.Empty(catalog.Graph.ConceptRegions(new GraphQuery(), parentRegionId: harbor).Regions);
        Assert.Equal(0, catalog.Graph.ClassificationAudit(new GraphQuery()).ProjectTypedCount);
    }

    [Fact]
    public async Task VerifierSupportAndOwnerAcceptanceAreDistinctAndRevocable()
    {
        using SqliteCatalog catalog = Open();
        relations.Add(("Client", "contains", "NodeOne"));
        CapturedOriginal source = await Enrich(catalog, "proposed.md", "Client contains NodeOne.");
        Assert.Empty(catalog.Graph.ConceptRegions(new GraphQuery()).Regions);
        Assert.Empty(catalog.Graph.ClassificationAudit(new GraphQuery()).Entries);

        catalog.Verification.SaveProvider(new VerificationProvider(VerificationProvider.Ollama, null,
            "qwen:9b", 0, 20, true, "http://127.0.0.1:11434"));
        Guid requestId = catalog.Verification.RequestForSource(source.SourceId)!.Value;
        VerificationRequestView approved = new VerificationApproval(catalog.Verification).Approve(requestId);
        var verdicts = approved.Items.Where(item => item.Included)
            .Select(item => new VerificationVerdict(item.Ordinal, "supported", null, null)).ToArray();
        Assert.True(catalog.Verification.Complete(catalog.Verification.Claim()!, new ScriptedChatModel(_ => "").Identity,
            new ChatResult("{}", 10, 4, TimeSpan.FromSeconds(1)), verdicts));

        GraphConceptRegion verified = Assert.Single(catalog.Graph.ConceptRegions(new GraphQuery()).Regions);
        Assert.Equal(1, verified.VerifiedMemberCount);
        Assert.Equal(0, verified.AcceptedMemberCount);
        Assert.Equal(1, Assert.Single(catalog.Graph.ClassificationAudit(new GraphQuery()).Entries).VerifierSupportedProjectMentions);

        Guid proposal = catalog.Proposals.List(source.SourceId).Single(item => item.Kind == "relation").ProposalId;
        Assert.True(catalog.Proposals.Review(proposal, "accepted"));
        GraphConceptRegion accepted = Assert.Single(catalog.Graph.ConceptRegions(new GraphQuery()).Regions);
        Assert.Equal(1, accepted.AcceptedMemberCount);
        Assert.Equal(0, accepted.VerifiedMemberCount);

        Assert.True(catalog.Proposals.Review(proposal, "rejected"));
        Assert.Empty(catalog.Graph.ConceptRegions(new GraphQuery()).Regions);
        Assert.Single(catalog.Graph.ClassificationAudit(new GraphQuery()).Entries);
    }

    [Fact]
    public async Task RegionProjectionCollapsesReciprocalEvidenceAndDoesNotLoopThroughContainmentCycles()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal source = await Enrich(catalog, "claims.md", "Client NodeOne NodeTwo NodeThree.");
        await Accept(catalog, source);
        Guid parent = Single(catalog, "Client").EntityId;
        Guid child = Single(catalog, "NodeOne").EntityId;
        Guid other = Single(catalog, "NodeTwo").EntityId;
        Guid leaf = Single(catalog, "NodeThree").EntityId;
        catalog.Knowledge.Relate(parent, "contains", child);
        catalog.Knowledge.Relate(child, "part_of", parent);
        catalog.Knowledge.Relate(child, "contains", other);
        catalog.Knowledge.Relate(other, "contains", child);
        catalog.Knowledge.Relate(child, "contains", leaf);

        Assert.Equal(1, Assert.Single(catalog.Graph.ConceptRegions(new GraphQuery()).Regions).ContainedEntityCount);
        Assert.Equal(child, Assert.Single(catalog.Graph.ConceptRegions(new GraphQuery(), parentRegionId: parent).Regions).RegionId);
        Assert.Empty(catalog.Graph.ConceptRegions(new GraphQuery(), parentRegionId: child).Regions);
        Assert.Equal(2, catalog.Graph.Neighborhood(parent, new GraphQuery()).Edges.Count);

        catalog.Knowledge.Relate(child, "contains", parent);
        Assert.Empty(catalog.Graph.ConceptRegions(new GraphQuery()).Regions);
        Assert.Empty(catalog.Graph.ConceptRegions(new GraphQuery(), parentRegionId: parent).Regions);
    }

    [Fact]
    public async Task RegionAndAuditPagesBindCursorsToScopeAndRevisionThroughDesktop()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal first = await Enrich(catalog, "first.md", "Client NodeOne.");
        CapturedOriginal second = await Enrich(catalog, "second.md", "C3Reader PHY.");
        await Accept(catalog, first);
        await Accept(catalog, second);
        Guid client = Single(catalog, "Client").EntityId;
        catalog.Knowledge.Relate(client, "contains", Single(catalog, "NodeOne").EntityId);
        catalog.Knowledge.Relate(Single(catalog, "C3Reader").EntityId, "contains", Single(catalog, "PHY").EntityId);

        GraphQuery query = new(Limit: 1);
        GraphConceptPage page = catalog.Graph.ConceptRegions(query);
        GraphClassificationAuditPage audit = catalog.Graph.ClassificationAudit(query);
        Assert.Equal(2, page.RegionCount);
        Assert.Equal(2, audit.ProjectTypedCount);
        Assert.NotNull(page.NextCursor);
        Assert.NotNull(audit.NextCursor);
        GraphConceptPage next = catalog.Graph.ConceptRegions(query with { Cursor = page.NextCursor });
        Assert.Equal(page.Revision, next.Revision);
        Assert.NotEqual(Assert.Single(page.Regions).RegionId, Assert.Single(next.Regions).RegionId);
        Assert.Null(next.NextCursor);
        Assert.Null(catalog.Graph.ClassificationAudit(query with { Cursor = audit.NextCursor }).NextCursor);
        Assert.Throws<ArgumentException>(() => catalog.Graph.ConceptRegions(query with { Cursor = page.NextCursor }, parentRegionId: client));
        Assert.Throws<ArgumentException>(() => catalog.Graph.ClassificationAudit(query with { Cursor = page.NextCursor }));
        Assert.Throws<ArgumentException>(() => catalog.Graph.ConceptRegions(query with { IncludeSuggestions = true }));
        Assert.Throws<ArgumentException>(() => catalog.Graph.ConceptRegions(query with { EntityType = "project" }));

        DesktopProtocol desktop = TestDesktop.Protocol(catalog);
        async Task<JsonElement> Request(string action, Guid? parentRegionId = null)
        {
            string request = parentRegionId is Guid parent
                ? JsonSerializer.Serialize(new { requestId = "regions", action, sourceId = first.SourceId, parentRegionId = parent })
                : JsonSerializer.Serialize(new { requestId = "regions", action, sourceId = first.SourceId });
            using JsonDocument result = JsonDocument.Parse(await desktop.HandleAsync(request));
            Assert.True(result.RootElement.GetProperty("ok").GetBoolean(), result.RootElement.ToString());
            return result.RootElement.GetProperty("data").Clone();
        }

        JsonElement regions = await Request("graphConceptRegions");
        Assert.Equal(1, regions.GetProperty("regionCount").GetInt32());
        Assert.Equal(client.ToString("D"), regions.GetProperty("regions")[0].GetProperty("regionId").GetString());
        Assert.Empty((await Request("graphConceptRegions", client)).GetProperty("regions").EnumerateArray());
        Assert.Equal(1, (await Request("graphClassificationAudit")).GetProperty("projectTypedCount").GetInt32());

        Assert.True(catalog.Knowledge.Rename(client, "Client Workspace"));
        Assert.Equal("Client Workspace", Assert.Single(catalog.Graph.ConceptRegions(new GraphQuery(SourceId: first.SourceId)).Regions).Label);
        Assert.Throws<InvalidOperationException>(() => catalog.Graph.ConceptRegions(query with { Cursor = page.NextCursor }));
        Assert.Throws<InvalidOperationException>(() => catalog.Graph.ClassificationAudit(query with { Cursor = audit.NextCursor }));
        using CancellationTokenSource stopped = new();
        stopped.Cancel();
        Assert.Throws<OperationCanceledException>(() => catalog.Graph.ConceptRegions(query, stopped.Token));

        Assert.True(catalog.Sources.Revoke(first.SourceId));
        Assert.Equal(0, (await Request("graphConceptRegions")).GetProperty("regionCount").GetInt32());
        Assert.Equal(0, (await Request("graphClassificationAudit")).GetProperty("projectTypedCount").GetInt32());
    }
}
