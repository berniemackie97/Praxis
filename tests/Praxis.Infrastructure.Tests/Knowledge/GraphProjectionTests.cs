using System.Text;
using System.Text.Json;
using Praxis.Application.Desktop;
using Praxis.Application.Inference;
using Praxis.Application.Knowledge;
using Praxis.Application.Text;
using Praxis.Application.Verification;
using Praxis.Domain.Knowledge;
using Praxis.Domain.Models;
using Praxis.Domain.Originals;
using Praxis.Domain.Verification;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Knowledge;

public sealed class GraphProjectionTests : IDisposable
{
    private readonly string workspace = Path.Combine(TestPaths.Temp, "praxis-graph-" + Guid.NewGuid().ToString("N"));
    private readonly string root;
    private readonly Dictionary<string, string> types = new(StringComparer.Ordinal)
    {
        ["C3Reader"] = "project",
        ["PHY"] = "technology",
        ["Alex Rivera"] = "person",
        ["Client"] = "project",
        ["NodeOne"] = "concept",
        ["NodeTwo"] = "concept",
        ["NodeThree"] = "concept",
        ["NodeFour"] = "concept",
        ["NodeFive"] = "concept",
        ["NodeSix"] = "concept"
    };
    private readonly List<(string Subject, string Predicate, string Object)> relations = [];

    public GraphProjectionTests()
    {
        root = Path.Combine(workspace, "catalog");
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(workspace, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task UnifiedOverviewCountsScopedCurrentEntitiesRatherThanRandomSeeds()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal first = await Enrich(catalog, "reader.md", "C3Reader parses PHY.");
        await Accept(catalog, first);
        CapturedOriginal second = await Enrich(catalog, "people.md", "Alex Rivera works on Client.");
        await Accept(catalog, second);

        GraphOverview all = catalog.Graph.Overview(new GraphQuery());
        Assert.Equal(4, all.EntityCount);
        Assert.Equal(all.EntityCount, all.Types.Sum(type => type.Count));
        Assert.Contains(all.Types, type => type.EntityType == "project" && type.Count == 2);
        Assert.Contains(all.Types, type => type.EntityType == "technology" && type.Count == 1);
        Assert.Contains(all.Types, type => type.EntityType == "person" && type.Count == 1);

        GraphOverview scoped = catalog.Graph.Overview(new GraphQuery(SourceId: first.SourceId));
        Assert.Equal(2, scoped.EntityCount);
        Assert.DoesNotContain(scoped.Types, type => type.EntityType == "person");

        Guid collection = catalog.Collections.Create("One source").CollectionId;
        Assert.True(catalog.Collections.AddSource(collection, first.SourceId));
        Assert.Equal(2, catalog.Graph.Overview(new GraphQuery(CollectionId: collection)).EntityCount);
        Assert.True(catalog.Collections.Archive(collection));
        Assert.Equal(0, catalog.Graph.Overview(new GraphQuery(CollectionId: collection)).EntityCount);

        Assert.True(catalog.Sources.Revoke(first.SourceId));
        Assert.Equal(2, catalog.Graph.Overview(new GraphQuery()).EntityCount);
        Assert.Empty(catalog.Graph.Overview(new GraphQuery(SourceId: first.SourceId)).Types);
    }

    [Fact]
    public async Task OverviewNeverPromotesPendingOrRevokedEvidenceIntoConfirmedCounts()
    {
        using SqliteCatalog catalog = Open();
        relations.Add(("C3Reader", "parses", "PHY"));
        CapturedOriginal captured = await Enrich(catalog, "pending.md", "C3Reader parses PHY.");
        Assert.Equal(0, catalog.Graph.Overview(new GraphQuery()).EntityCount);
        Assert.Equal(2, catalog.Graph.Overview(new GraphQuery(IncludeSuggestions: true)).EntityCount);

        await Accept(catalog, captured);
        Assert.Equal(2, catalog.Graph.Overview(new GraphQuery()).EntityCount);
        Assert.Throws<ArgumentException>(() => catalog.Graph.Overview(new GraphQuery(Cursor: "stale")));
        Assert.Throws<ArgumentException>(() => catalog.Graph.Overview(new GraphQuery(EntityType: "project")));
        Assert.True(catalog.Sources.Revoke(captured.SourceId));
        Assert.Equal(0, catalog.Graph.Overview(new GraphQuery(IncludeSuggestions: true)).EntityCount);
    }

    [Fact]
    public async Task DesktopOverviewRespectsSourceScopeAndUsesTheSameCurrentReader()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal captured = await Enrich(catalog, "reader.md", "C3Reader parses PHY.");
        await Accept(catalog, captured);
        DesktopProtocol desktop = TestDesktop.Protocol(catalog);
        string request = JsonSerializer.Serialize(new
        {
            requestId = "overview", action = "graphOverview", sourceId = captured.SourceId
        });
        using JsonDocument result = JsonDocument.Parse(await desktop.HandleAsync(request));
        Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(2, result.RootElement.GetProperty("data").GetProperty("entityCount").GetInt32());

        Assert.True(catalog.Sources.Revoke(captured.SourceId));
        using JsonDocument revoked = JsonDocument.Parse(await desktop.HandleAsync(request));
        Assert.True(revoked.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(0, revoked.RootElement.GetProperty("data").GetProperty("entityCount").GetInt32());
    }

    [Fact]
    public async Task PendingKnowledgeIsNotAConfirmedGraphNodeOrEdge()
    {
        using SqliteCatalog catalog = Open();
        relations.Add(("C3Reader", "parses", "PHY"));
        CapturedOriginal captured = await Enrich(catalog, "reader.md", "C3Reader parses PHY.");
        Guid id = Single(catalog, "C3Reader").EntityId;

        Assert.Equal("empty", catalog.Graph.Seeds("C3Reader", new GraphQuery()).Outcome);
        Assert.Equal("unavailable", catalog.Graph.Neighborhood(id, new GraphQuery()).Outcome);

        await Accept(catalog, captured);
        GraphNode node = Assert.Single(catalog.Graph.Seeds("C3Reader", new GraphQuery()).Nodes);
        Assert.Equal("C3Reader", node.Label);
        Assert.Equal("project", node.EntityType);
        Assert.Equal("confirmed", node.Status);
        Assert.Equal(captured.SourceId, node.Evidence!.SourceId);
        Assert.Equal(captured.ItemId, node.Evidence.ItemId);
        Assert.Equal(captured.Revision, node.Evidence.Revision);
        Assert.Equal(captured.Sha256, node.Evidence.SourceDigest);

        GraphPage neighbors = catalog.Graph.Neighborhood(id, new GraphQuery());
        GraphEdge edge = Assert.Single(neighbors.Edges);
        Assert.Equal("parses", edge.Predicate);
        Assert.Equal(id, edge.SubjectId);
        Assert.Equal(Single(catalog, "PHY").EntityId, edge.ObjectId);
        Assert.Equal("accepted", edge.EvidenceStatus);
        Assert.Equal(captured.Sha256, edge.Evidence!.SourceDigest);
        Assert.Equal("ready", neighbors.Outcome);
        Assert.Null(neighbors.NextCursor);
    }

    [Fact]
    public async Task CollectionAndSourceFiltersDoNotRevealOtherNamesOrRelations()
    {
        using SqliteCatalog catalog = Open();
        relations.Add(("C3Reader", "parses", "PHY"));
        CapturedOriginal approved = await Enrich(catalog, "source.md", "C3Reader parses PHY.");
        await Accept(catalog, approved);
        CapturedOriginal separate = await Enrich(catalog, "private.md", "Alex Rivera works on Client.");
        await Accept(catalog, separate);

        Guid selected = catalog.Collections.Create("Selected evidence").CollectionId;
        Assert.True(catalog.Collections.AddSource(selected, approved.SourceId));
        GraphQuery scope = new(CollectionId: selected);

        Assert.Equal("empty", catalog.Graph.Seeds("Alex Rivera", scope).Outcome);
        Assert.DoesNotContain(catalog.Graph.Seeds(null, scope).Nodes, n => n.Label == "Alex Rivera");
        Assert.Equal("unavailable", catalog.Graph.Neighborhood(Single(catalog, "Alex Rivera").EntityId, scope).Outcome);
        Assert.Equal("ready", catalog.Graph.Neighborhood(Single(catalog, "C3Reader").EntityId, scope).Outcome);
        Assert.All(catalog.Graph.Neighborhood(Single(catalog, "C3Reader").EntityId, scope).Nodes,
            node => Assert.Equal(approved.SourceId, node.Evidence!.SourceId));

        Assert.Equal("unavailable", catalog.Graph.Neighborhood(Single(catalog, "C3Reader").EntityId,
            new GraphQuery(SourceId: separate.SourceId)).Outcome);
        Assert.True(catalog.Collections.RemoveSource(selected, approved.SourceId));
        Assert.Equal("empty", catalog.Graph.Seeds(null, scope).Outcome);
        Assert.True(catalog.Collections.AddSource(selected, approved.SourceId));
        Assert.True(catalog.Collections.Archive(selected));
        Assert.Equal("empty", catalog.Graph.Seeds(null, scope).Outcome);
    }

    [Fact]
    public async Task RevocationAndNewRevisionRemoveOldEvidenceEvenAfterOwnerTypeChange()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal first = await Enrich(catalog, "reader.md", "C3Reader is a parser.");
        await Accept(catalog, first);
        Guid id = Single(catalog, "C3Reader").EntityId;
        Assert.True(catalog.Knowledge.SetType(id, "project"));

        using (MemoryStream updated = new(Encoding.UTF8.GetBytes("There are no known entities here.")))
        {
            CapturedOriginal revised = await catalog.Originals.CaptureFileAsync(
                Path.Combine(workspace, "reader.md"), updated, 200_000, "revision");
            Assert.Equal(first.ItemId, revised.ItemId);
            Assert.Equal(2, revised.Revision);
        }
        Assert.Equal("empty", catalog.Graph.Seeds(null, new GraphQuery()).Outcome);
        Assert.Equal("unavailable", catalog.Graph.Neighborhood(id, new GraphQuery()).Outcome);

        CapturedOriginal another = await Enrich(catalog, "second.md", "C3Reader is a parser.");
        await Accept(catalog, another);
        Assert.Single(catalog.Graph.Seeds("C3Reader", new GraphQuery()).Nodes);
        Assert.True(catalog.Sources.Revoke(another.SourceId));
        Assert.Equal("empty", catalog.Graph.Seeds("C3Reader", new GraphQuery()).Outcome);
    }

    [Fact]
    public async Task OwnerEdgesHaveNoInventedSourceEvidenceAndArePagedAtOneHopOnly()
    {
        using SqliteCatalog catalog = Open();
        const string text = "Client NodeOne NodeTwo NodeThree NodeFour NodeFive NodeSix.";
        CapturedOriginal source = await Enrich(catalog, "all.md", text);
        await Accept(catalog, source);
        Guid center = Single(catalog, "Client").EntityId;

        foreach (string name in new[] { "NodeOne", "NodeTwo", "NodeThree", "NodeFour", "NodeFive", "NodeSix" })
        {
            catalog.Knowledge.Relate(center, "uses", Single(catalog, name).EntityId);
        }

        GraphQuery request = new(Limit: 2);
        GraphPage first = catalog.Graph.Neighborhood(center, request);
        Assert.Equal(2, first.Edges.Count);
        Assert.Equal(3, first.Nodes.Count);
        Assert.NotNull(first.NextCursor);
        Assert.All(first.Edges, edge =>
        {
            Assert.Equal("owner", edge.EvidenceStatus);
            Assert.Null(edge.Evidence);
        });

        GraphPage second = catalog.Graph.Neighborhood(center, request with { Cursor = first.NextCursor });
        GraphPage third = catalog.Graph.Neighborhood(center, request with { Cursor = second.NextCursor });
        Assert.Equal(2, second.Edges.Count);
        Assert.Equal(2, third.Edges.Count);
        Assert.Null(third.NextCursor);
        Assert.Equal(6, first.Edges.Concat(second.Edges).Concat(third.Edges)
            .Select(edge => edge.RelationId).Distinct().Count());
        Assert.All(first.Edges.Concat(second.Edges).Concat(third.Edges),
            edge => Assert.Equal(center, edge.SubjectId));

        GraphPage filtered = catalog.Graph.Neighborhood(center, new GraphQuery(Predicate: "parses"));
        Assert.Empty(filtered.Edges);
        Assert.Single(filtered.Nodes);
    }

    [Fact]
    public async Task RelationshipInspectionRechecksScopeOwnerEvidenceAndRevocation()
    {
        using SqliteCatalog catalog = Open();
        relations.Add(("C3Reader", "parses", "PHY"));
        CapturedOriginal captured = await Enrich(catalog, "reader.md", "C3Reader parses PHY.");
        await Accept(catalog, captured);
        Guid subject = Single(catalog, "C3Reader").EntityId;
        Guid other = Single(catalog, "PHY").EntityId;
        GraphEdge relation = Assert.Single(catalog.Graph.Neighborhood(subject, new GraphQuery()).Edges);

        GraphPage inspection = catalog.Graph.Relation(subject, relation.RelationId, new GraphQuery());
        GraphEdge current = Assert.Single(inspection.Edges);
        Assert.Equal(relation.RelationId, current.RelationId);
        Assert.Equal(captured.SourceId, current.Evidence!.SourceId);
        Assert.Equal(captured.Sha256, current.Evidence.SourceDigest);
        Assert.Equal(2, inspection.Nodes.Count);
        Assert.Null(inspection.NextCursor);

        Assert.Equal("unavailable", catalog.Graph.Relation(subject, Guid.NewGuid(), new GraphQuery()).Outcome);
        Assert.Equal("unavailable", catalog.Graph.Relation(Guid.NewGuid(), relation.RelationId, new GraphQuery()).Outcome);
        Assert.Throws<ArgumentException>(() => catalog.Graph.Relation(subject, relation.RelationId,
            new GraphQuery(Cursor: "invalid")));
        Assert.Equal("unavailable", catalog.Graph.Relation(subject, relation.RelationId,
            new GraphQuery(SourceId: Guid.NewGuid())).Outcome);

        Guid ownerRelation = catalog.Knowledge.Relate(other, "references", subject);
        GraphEdge owner = Assert.Single(catalog.Graph.Relation(subject, ownerRelation, new GraphQuery()).Edges);
        Assert.Equal("owner", owner.EvidenceStatus);
        Assert.Null(owner.Evidence);

        Assert.True(catalog.Proposals.Review(relation.Evidence!.ProposalId, "rejected"));
        Assert.Equal("unavailable", catalog.Graph.Relation(subject, relation.RelationId, new GraphQuery()).Outcome);
        Assert.True(catalog.Sources.Revoke(captured.SourceId));
        Assert.Equal("unavailable", catalog.Graph.Relation(subject, ownerRelation, new GraphQuery()).Outcome);
    }

    [Fact]
    public async Task DesktopRelationshipInspectionUsesTheSameCurrentGraphScope()
    {
        using SqliteCatalog catalog = Open();
        relations.Add(("C3Reader", "uses", "PHY"));
        CapturedOriginal captured = await Enrich(catalog, "reader.md", "C3Reader uses PHY.");
        await Accept(catalog, captured);
        Guid subject = Single(catalog, "C3Reader").EntityId;
        GraphEdge relation = Assert.Single(catalog.Graph.Neighborhood(subject, new GraphQuery()).Edges);
        DesktopProtocol protocol = TestDesktop.Protocol(catalog);
        string request = JsonSerializer.Serialize(new
        {
            requestId = "inspection",
            action = "graphRelation",
            entityId = subject,
            relationId = relation.RelationId
        });
        using JsonDocument response = JsonDocument.Parse(await protocol.HandleAsync(request));
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(relation.RelationId.ToString("D"),
            response.RootElement.GetProperty("data").GetProperty("edges")[0]
                .GetProperty("relationId").GetString());

        string previewRequest = JsonSerializer.Serialize(new
        {
            requestId = "preview",
            action = "preview",
            sourceId = captured.SourceId,
            itemId = captured.ItemId,
            revision = captured.Revision,
            originalSha256 = captured.Sha256
        });
        using JsonDocument preview = JsonDocument.Parse(await protocol.HandleAsync(previewRequest));
        Assert.True(preview.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("C3Reader uses PHY.", preview.RootElement.GetProperty("data")
            .GetProperty("content").GetString());

        Assert.True(catalog.Sources.Revoke(captured.SourceId));
        using JsonDocument denied = JsonDocument.Parse(await protocol.HandleAsync(request));
        Assert.True(denied.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("unavailable", denied.RootElement.GetProperty("data").GetProperty("outcome").GetString());
        Assert.Empty(denied.RootElement.GetProperty("data").GetProperty("edges").EnumerateArray());

        using JsonDocument revokedPreview = JsonDocument.Parse(await protocol.HandleAsync(previewRequest));
        Assert.True(revokedPreview.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("unavailable", revokedPreview.RootElement.GetProperty("data")
            .GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task MultipleSourcesRemainSeparateAndRevocationOnlyHidesAffectedRelations()
    {
        using SqliteCatalog catalog = Open();
        relations.Add(("C3Reader", "parses", "PHY"));
        CapturedOriginal first = await Enrich(catalog, "first.md", "C3Reader parses PHY.");
        await Accept(catalog, first);
        CapturedOriginal second = await Enrich(catalog, "second.md", "C3Reader parses PHY.");
        await Accept(catalog, second);
        Guid focus = Single(catalog, "C3Reader").EntityId;
        GraphPage both = catalog.Graph.Neighborhood(focus, new GraphQuery());
        Assert.Equal(2, both.Edges.Count);
        Assert.Equal(2, both.Edges.Select(edge => edge.RelationId).Distinct().Count());
        Assert.Equal(new[] { first.SourceId, second.SourceId }.Order().ToArray(),
            both.Edges.Select(edge => edge.Evidence!.SourceId).Order().ToArray());

        Assert.True(catalog.Sources.Revoke(first.SourceId));
        GraphEdge remaining = Assert.Single(catalog.Graph.Neighborhood(focus, new GraphQuery()).Edges);
        Assert.Equal(second.SourceId, remaining.Evidence!.SourceId);
        GraphEdge expired = both.Edges.Single(edge => edge.Evidence!.SourceId == first.SourceId);
        Assert.Equal("unavailable", catalog.Graph.Relation(focus, expired.RelationId, new GraphQuery()).Outcome);
        Assert.Equal(remaining.RelationId,
            Assert.Single(catalog.Graph.Relation(focus, remaining.RelationId, new GraphQuery()).Edges).RelationId);
    }

    [Fact]
    public async Task UnsupportedVerifierVerdictIsOnlyVisibleInExplicitContradictionMode()
    {
        using SqliteCatalog catalog = Open();
        relations.Add(("C3Reader", "parses", "PHY"));
        CapturedOriginal source = await Enrich(catalog, "claims.md", "C3Reader parses PHY.");
        Guid focus = Single(catalog, "C3Reader").EntityId;
        Guid relationProposal = catalog.Proposals.List(source.SourceId)
            .Single(item => item.Kind == "relation").ProposalId;

        catalog.Verification.SaveProvider(new VerificationProvider(VerificationProvider.Ollama, null,
            "qwen:9b", 0, 20, true, "http://127.0.0.1:11434"));
        Guid requestId = catalog.Verification.RequestForSource(source.SourceId)!.Value;
        VerificationRequestView approved = new VerificationApproval(catalog.Verification).Approve(requestId);
        var verdicts = approved.Items.Where(item => item.Included).Select(item =>
            new VerificationVerdict(item.Ordinal,
                item.ProposalId == relationProposal ? "unsupported" : "supported", null, null)).ToArray();
        Assert.Contains(approved.Items, item => item.ProposalId == relationProposal);
        var claim = catalog.Verification.Claim()!;
        Assert.True(catalog.Verification.Complete(claim, new ScriptedChatModel(_ => "").Identity,
            new ChatResult("{}", 10, 4, TimeSpan.FromSeconds(1)), verdicts));

        Assert.Empty(catalog.Graph.Neighborhood(focus, new GraphQuery()).Edges);
        Assert.Throws<ArgumentException>(() => catalog.Graph.Neighborhood(focus,
            new GraphQuery(EvidenceStatus: "contradicted")));
        GraphEdge contradicted = Assert.Single(catalog.Graph.Neighborhood(focus,
            new GraphQuery(IncludeSuggestions: true, EvidenceStatus: "contradicted")).Edges);
        Assert.Equal("contradicted", contradicted.EvidenceStatus);
        Assert.Equal("unsupported", contradicted.Evidence!.Verdict);

        Assert.True(catalog.Proposals.Review(relationProposal, "accepted"));
        GraphEdge ownerApproved = Assert.Single(catalog.Graph.Neighborhood(focus, new GraphQuery()).Edges);
        Assert.Equal("accepted", ownerApproved.EvidenceStatus);
        Assert.Empty(catalog.Graph.Neighborhood(focus,
            new GraphQuery(IncludeSuggestions: true, EvidenceStatus: "contradicted")).Edges);
    }

    [Fact]
    public async Task GraphPagesRejectStaleOrMismatchedCursors()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal source = await Enrich(catalog, "all.md", "Client NodeOne NodeTwo NodeThree.");
        await Accept(catalog, source);
        Guid center = Single(catalog, "Client").EntityId;
        foreach (string name in new[] { "NodeOne", "NodeTwo", "NodeThree" })
        {
            catalog.Knowledge.Relate(center, "uses", Single(catalog, name).EntityId);
        }

        GraphPage first = catalog.Graph.Neighborhood(center, new GraphQuery(Limit: 1));
        Assert.NotNull(first.NextCursor);
        Assert.Throws<ArgumentException>(() => catalog.Graph.Neighborhood(center,
            new GraphQuery(Limit: 1, Cursor: first.NextCursor, Predicate: "uses")));
        Assert.Throws<ArgumentException>(() => catalog.Graph.Neighborhood(center,
            new GraphQuery(Cursor: "invalid")));

        Assert.True(catalog.Knowledge.Rename(center, "Client Project"));
        Assert.Throws<InvalidOperationException>(() => catalog.Graph.Neighborhood(center,
            new GraphQuery(Limit: 1, Cursor: first.NextCursor)));
    }

    [Fact]
    public async Task DesktopGraphQueriesValidateLimitsAndRespectCancellation()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal source = await Enrich(catalog, "reader.md", "C3Reader parses PHY.");
        await Accept(catalog, source);
        DesktopProtocol protocol = TestDesktop.Protocol(catalog);
        using JsonDocument page = JsonDocument.Parse(await protocol.HandleAsync(
            """{"requestId":"1","action":"graphSeeds","search":"C3Reader","limit":1}"""));
        Assert.True(page.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("C3Reader", page.RootElement.GetProperty("data")
            .GetProperty("nodes")[0].GetProperty("label").GetString());

        using JsonDocument tooLarge = JsonDocument.Parse(await protocol.HandleAsync(
            """{"requestId":"2","action":"graphSeeds","limit":101}"""));
        Assert.False(tooLarge.RootElement.GetProperty("ok").GetBoolean());

        using JsonDocument badStatus = JsonDocument.Parse(await protocol.HandleAsync(
            """{"requestId":"3","action":"graphNeighborhood","entityId":"00000000-0000-0000-0000-000000000001","evidenceStatus":"pending"}"""));
        Assert.False(badStatus.RootElement.GetProperty("ok").GetBoolean());

        using CancellationTokenSource canceled = new();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => catalog.Graph.Seeds(null, new GraphQuery(), canceled.Token));
    }

    [Fact]
    public async Task ExpansionStopsAtOneHopAndRejectedRelationIsRemoved()
    {
        using SqliteCatalog catalog = Open();
        relations.Add(("C3Reader", "uses", "PHY"));
        CapturedOriginal first = await Enrich(catalog, "first.md", "C3Reader uses PHY.");
        await Accept(catalog, first);
        relations.Clear();
        relations.Add(("PHY", "part_of", "Client"));
        CapturedOriginal second = await Enrich(catalog, "second.md", "PHY is part of Client.");
        await Accept(catalog, second);

        Guid reader = Single(catalog, "C3Reader").EntityId;
        GraphPage neighborhood = catalog.Graph.Neighborhood(reader, new GraphQuery());
        Assert.Single(neighborhood.Edges);
        Assert.Contains(neighborhood.Nodes, node => node.Label == "PHY");
        Assert.DoesNotContain(neighborhood.Nodes, node => node.Label == "Client");

        Guid proposal = catalog.Proposals.List(first.SourceId)
            .Single(item => item.Kind == "relation").ProposalId;
        Assert.True(catalog.Proposals.Review(proposal, "rejected"));
        GraphPage removed = catalog.Graph.Neighborhood(reader, new GraphQuery());
        Assert.Empty(removed.Edges);
        Assert.Single(removed.Nodes);
        Assert.Equal("ready", removed.Outcome);

        Assert.Empty(catalog.Graph.Neighborhood(Single(catalog, "PHY").EntityId,
            new GraphQuery(SourceId: first.SourceId)).Edges);
    }

    [Fact]
    public async Task TypeProjectionAndCountsUseOnlyTheSelectedConfirmedSources()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal first = await Enrich(catalog, "first.md", "PHY is documented as technology.");
        await Accept(catalog, first);
        types["PHY"] = "concept";
        CapturedOriginal second = await Enrich(catalog, "second.md", "PHY is described as concept.");
        await Accept(catalog, second);

        GraphNode unscoped = Assert.Single(catalog.Graph.Seeds("PHY", new GraphQuery()).Nodes);
        Assert.Equal(2, unscoped.Mentions);
        Assert.Equal(2, unscoped.Sources);
        Assert.True(unscoped.TypeConflict);
        Assert.Equal("concept", unscoped.EntityType);

        GraphNode scoped = Assert.Single(catalog.Graph.Seeds("PHY", new GraphQuery(SourceId: first.SourceId)).Nodes);
        Assert.Equal("technology", scoped.EntityType);
        Assert.Equal(1, scoped.Sources);
        Assert.Equal(1, scoped.Mentions);
        Assert.False(scoped.TypeConflict);
        Assert.Equal(first.SourceId, scoped.Evidence!.SourceId);
        Assert.NotEqual(second.SourceId, scoped.Evidence.SourceId);
    }

    [Fact]
    public async Task HighDegreePagesDoNotExpandBeyondTheRequestedLimit()
    {
        using SqliteCatalog catalog = Open();
        Guid center = Guid.Empty;
        for (int batch = 0; batch < 12; batch++)
        {
            string[] names = Enumerable.Range(batch * 10 + 1, 10)
                .Select(index => $"Item{index:D3}").ToArray();
            foreach (string name in names)
            {
                types.Add(name, "concept");
            }
            CapturedOriginal source = await Enrich(catalog, $"batch{batch:D2}.md",
                $"Client connects {string.Join(' ', names)}.");
            await Accept(catalog, source);
            center = Single(catalog, "Client").EntityId;
            foreach (string name in names)
            {
                catalog.Knowledge.Relate(center, "references", Single(catalog, name).EntityId);
            }
        }

        GraphOverview overview = catalog.Graph.Overview(new GraphQuery());
        Assert.Equal(121, overview.EntityCount);
        Assert.Equal(121, overview.Types.Sum(type => type.Count));

        GraphPage first = catalog.Graph.Neighborhood(center, new GraphQuery(Limit: 100));
        Assert.Equal(100, first.Edges.Count);
        Assert.Equal(101, first.Nodes.Count);
        Assert.NotNull(first.NextCursor);
        GraphPage last = catalog.Graph.Neighborhood(center, new GraphQuery(Limit: 100, Cursor: first.NextCursor));
        Assert.Equal(20, last.Edges.Count);
        Assert.Null(last.NextCursor);
        Assert.Equal(120, first.Edges.Concat(last.Edges)
            .Select(edge => edge.RelationId).Distinct().Count());
    }

    [Fact]
    public async Task MergeRejectAndRestoreUpdateOnlyTheCurrentDirectRelationships()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal source = await Enrich(catalog, "first.md", "C3Reader Client PHY.");
        await Accept(catalog, source);
        Guid reader = Single(catalog, "C3Reader").EntityId;
        Guid client = Single(catalog, "Client").EntityId;
        Guid phy = Single(catalog, "PHY").EntityId;
        catalog.Knowledge.Relate(reader, "uses", phy);

        Assert.True(catalog.Knowledge.Merge(reader, client));
        Assert.Equal("unavailable", catalog.Graph.Neighborhood(reader, new GraphQuery()).Outcome);
        GraphEdge redirected = Assert.Single(catalog.Graph.Neighborhood(client, new GraphQuery()).Edges);
        Assert.Equal(client, redirected.SubjectId);
        Assert.Equal(phy, redirected.ObjectId);

        Assert.True(catalog.Knowledge.Reject(phy));
        Assert.Empty(catalog.Graph.Neighborhood(client, new GraphQuery()).Edges);
        Assert.DoesNotContain(catalog.Graph.Seeds(null, new GraphQuery()).Nodes, node => node.EntityId == phy);

        Assert.True(catalog.Knowledge.Restore(phy));
        Assert.Single(catalog.Graph.Neighborhood(client, new GraphQuery()).Edges);
    }

    [Fact]
    public async Task ConcurrentCanonicalWriteInvalidatesBothGraphPageTypes()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal captured = await Enrich(catalog, "three.md", "Client NodeOne NodeTwo NodeThree.");
        await Accept(catalog, captured);
        Guid center = Single(catalog, "Client").EntityId;
        catalog.Knowledge.Relate(center, "uses", Single(catalog, "NodeOne").EntityId);
        catalog.Knowledge.Relate(center, "uses", Single(catalog, "NodeTwo").EntityId);

        GraphPage seeds = catalog.Graph.Seeds(null, new GraphQuery(Limit: 1));
        GraphPage neighbors = catalog.Graph.Neighborhood(center, new GraphQuery(Limit: 1));
        Assert.NotNull(seeds.NextCursor);
        Assert.NotNull(neighbors.NextCursor);

        await Task.Run(() => catalog.Knowledge.Relate(center, "uses", Single(catalog, "NodeThree").EntityId));
        Assert.Throws<InvalidOperationException>(() => catalog.Graph.Seeds(null,
            new GraphQuery(Limit: 1, Cursor: seeds.NextCursor)));
        Assert.Throws<InvalidOperationException>(() => catalog.Graph.Neighborhood(center,
            new GraphQuery(Limit: 1, Cursor: neighbors.NextCursor)));
    }

    [Fact]
    public async Task SuggestedEdgesNeedExplicitOptInAndNeverGainAuthorityFromRendering()
    {
        using SqliteCatalog catalog = Open();
        relations.Add(("C3Reader", "parses", "PHY"));
        CapturedOriginal captured = await Enrich(catalog, "review.md", "C3Reader parses PHY.");
        Guid center = Single(catalog, "C3Reader").EntityId;

        Assert.Equal("empty", catalog.Graph.Seeds("C3Reader", new GraphQuery()).Outcome);
        GraphNode suggestion = Assert.Single(catalog.Graph.Seeds("C3Reader",
            new GraphQuery(IncludeSuggestions: true)).Nodes);
        Assert.Equal("suggested", suggestion.Status);
        Assert.Equal("pending", suggestion.Evidence!.Status);

        GraphPage proposed = catalog.Graph.Neighborhood(center,
            new GraphQuery(IncludeSuggestions: true, EvidenceStatus: "suggested"));
        GraphEdge relation = Assert.Single(proposed.Edges);
        Assert.Equal("suggested", relation.EvidenceStatus);
        Assert.Equal("pending", relation.Evidence!.Status);
        Assert.Equal("unavailable", catalog.Graph.Neighborhood(center,
            new GraphQuery(IncludeSuggestions: true, SourceId: Guid.NewGuid())).Outcome);

        Guid proposal = catalog.Proposals.List(captured.SourceId)
            .Single(item => item.Kind == "relation").ProposalId;
        Assert.True(catalog.Proposals.Review(proposal, "rejected"));
        Assert.Empty(catalog.Graph.Neighborhood(center,
            new GraphQuery(IncludeSuggestions: true)).Edges);

        await Accept(catalog, captured);
        GraphNode accepted = Assert.Single(catalog.Graph.Seeds("C3Reader", new GraphQuery()).Nodes);
        Assert.Equal("confirmed", accepted.Status);
        Assert.Empty(catalog.Graph.Neighborhood(center, new GraphQuery()).Edges);
    }

    [Fact]
    public async Task HistoricalOwnerAliasDoesNotLeakThroughScopedSearch()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal source = await Enrich(catalog, "reader.md", "C3Reader parses code.");
        await Accept(catalog, source);
        Guid id = Single(catalog, "C3Reader").EntityId;

        Assert.True(catalog.Knowledge.Rename(id, "Retired Identifier"));
        Assert.True(catalog.Knowledge.Rename(id, "Current Identifier"));

        GraphQuery scoped = new(SourceId: source.SourceId);
        Assert.Equal("empty", catalog.Graph.Seeds("Retired Identifier", scoped).Outcome);
        Assert.Equal(id, Assert.Single(catalog.Graph.Seeds("Current Identifier", scoped).Nodes).EntityId);
        Assert.Equal(id, Assert.Single(catalog.Graph.Seeds("C3Reader", scoped).Nodes).EntityId);

        Assert.True(catalog.Sources.Revoke(source.SourceId));
        Assert.Equal("empty", catalog.Graph.Seeds("Current Identifier", scoped).Outcome);
    }

    private SqliteCatalog Open()
    {
        SqliteCatalog catalog = SqliteCatalog.Open(root);
        catalog.Models.Save(new ModelProfile(ModelProfile.LocalEnrichment,
            "ollama", "http://127.0.0.1:11434", "qwen:9b", 16_384, Enabled: true));
        return catalog;
    }

    private async Task<CapturedOriginal> Enrich(SqliteCatalog catalog, string name, string body)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(body));
        CapturedOriginal captured = await catalog.Originals.CaptureFileAsync(
            Path.Combine(workspace, name), stream, 200_000, Guid.NewGuid().ToString("N"));
        ScriptedChatModel model = new(request =>
        {
            string excerpt = ScriptedChatModel.Excerpt(request);
            var entities = types.Where(entry => excerpt.Contains(entry.Key, StringComparison.Ordinal))
                .Select(entry => new { name = entry.Key, type = entry.Value, quote = entry.Key }).ToArray();
            var links = relations
                .Where(relation => excerpt.Contains(relation.Subject, StringComparison.Ordinal) &&
                                   excerpt.Contains(relation.Object, StringComparison.Ordinal))
                .Select(relation =>
                {
                    int start = Math.Min(excerpt.IndexOf(relation.Subject, StringComparison.Ordinal),
                        excerpt.IndexOf(relation.Object, StringComparison.Ordinal));
                    int end = Math.Max(excerpt.IndexOf(relation.Subject, StringComparison.Ordinal) + relation.Subject.Length,
                        excerpt.IndexOf(relation.Object, StringComparison.Ordinal) + relation.Object.Length);
                    return new
                    {
                        subject = relation.Subject,
                        predicate = relation.Predicate,
                        @object = relation.Object,
                        quote = excerpt[start..end]
                    };
                }).ToArray();
            return JsonSerializer.Serialize(new { topics = Array.Empty<object>(), entities, relations = links });
        });
        ProcessingDriver[] drivers =
        [
            ProcessingDriver.Text(catalog),
            new ProcessingDriver(catalog, new TextSegmentationHandler(catalog.Text, catalog.Segments, catalog.Processing)),
            new ProcessingDriver(catalog, TestHandlers.SemanticExtraction(catalog, _ => new ModelConnection(model)))
        ];
        foreach (ProcessingDriver driver in drivers)
        {
            while (await driver.ProcessNextAsync())
            {
            }
        }
        return captured;
    }

    private static async Task Accept(SqliteCatalog catalog, CapturedOriginal source)
    {
        DesktopProtocol protocol = TestDesktop.Protocol(catalog);
        using JsonDocument response = JsonDocument.Parse(await protocol.HandleAsync(
            $$"""{"requestId":"accept","action":"topicReviewAll","sourceId":"{{source.SourceId:D}}","decision":"accepted"}"""));
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
    }

    private static EntitySummary Single(SqliteCatalog catalog, string name) =>
        catalog.Knowledge.ListEntities(search: name, includeSuggestions: true)
            .Single(entity => entity.Name == name);

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
