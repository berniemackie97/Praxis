using Microsoft.Data.Sqlite;
using Praxis.Application.Knowledge;
using Praxis.Domain.Knowledge;
using Praxis.Domain.Originals;
using Praxis.Infrastructure.Persistence;
using Xunit;

namespace Praxis.Infrastructure.Tests.Knowledge;

public sealed partial class GraphProjectionTests
{
    [Fact]
    public async Task SpellingVariantsOfOneNameAreProposedForMergeIntoTheBetterKnownEntity()
    {
        using SqliteCatalog catalog = Open();
        types["GameServer"] = "project";
        types["game server"] = "project";
        await Enrich(catalog, "one.md", "GameServer handles logins.");
        await Enrich(catalog, "two.md", "GameServer spawns maps.");
        await Enrich(catalog, "three.md", "The game server restarts nightly.");
        Drain(catalog);

        KnowledgeRevision proposal = Assert.Single(catalog.Reconsolidation.Revisions(RevisionVocabulary.Suggested, 10),
            item => item.Field == RevisionVocabulary.Merge);
        Assert.Equal(("game server", "GameServer", RevisionVocabulary.Duplicates),
            (proposal.EntityName, proposal.AfterLabel, proposal.Trigger));

        RevisionReview review = new(catalog.Reconsolidation, catalog.Knowledge, catalog.Proposals);
        Assert.True(review.Decide(proposal.RevisionId, accept: true));
        Assert.Equal(3, Single(catalog, "GameServer").Mentions);
        Assert.DoesNotContain(catalog.Knowledge.ListEntities(null, "game server", 10, includeSuggestions: true),
            entity => entity.Name == "game server" && entity.Status == "active");
    }

    [Fact]
    public async Task ARelationItsEndpointsConfirmedTypesRuleOutIsProposedForRetraction()
    {
        using SqliteCatalog catalog = Open();
        types["Roadmap"] = "document";
        types["Parser"] = "function";
        relations.Add(("Roadmap", "calls", "Parser"));
        CapturedOriginal source = await Enrich(catalog, "plan.md", "Roadmap calls Parser.");
        Guid roadmap = Single(catalog, "Roadmap").EntityId;
        Assert.True(catalog.Knowledge.SetType(roadmap, "document"));
        Drain(catalog);

        KnowledgeRevision proposal = Assert.Single(catalog.Reconsolidation.Revisions(RevisionVocabulary.Suggested, 10),
            item => item.Field == RevisionVocabulary.Relation);
        Assert.Equal(("Roadmap", RevisionVocabulary.Retract), (proposal.EntityName, proposal.After));
        Assert.Contains("retract: Roadmap calls Parser", proposal.AfterLabel, StringComparison.Ordinal);

        Assert.Single(catalog.Graph.Neighborhood(roadmap, new GraphQuery(IncludeSuggestions: true, SourceId: source.SourceId)).Edges);
        Assert.True(new RevisionReview(catalog.Reconsolidation, catalog.Knowledge, catalog.Proposals).Decide(proposal.RevisionId, accept: true));
        Assert.Empty(catalog.Graph.Neighborhood(roadmap, new GraphQuery(IncludeSuggestions: true, SourceId: source.SourceId)).Edges);
    }

    [Fact]
    public async Task TheKnowledgeWatermarkMovesForKnowledgeNotForProcessingChurn()
    {
        using SqliteCatalog catalog = Open();
        CapturedOriginal source = await Enrich(catalog, "client.md", "Client NodeOne NodeTwo NodeThree.");
        await Accept(catalog, source);
        catalog.Knowledge.Relate(Single(catalog, "Client").EntityId, "references", Single(catalog, "NodeOne").EntityId);
        catalog.Knowledge.Relate(Single(catalog, "Client").EntityId, "references", Single(catalog, "NodeTwo").EntityId);
        (long revision, long latest) = catalog.Reconsolidation.Watermark();

        GraphPage first = catalog.Graph.Atlas(new GraphQuery(Limit: 1));
        Assert.NotNull(first.NextCursor);
        using (SqliteConnection connection = new($"Data Source={catalog.DatabasePath}"))
        {
            connection.Open();
            using SqliteCommand churn = connection.CreateCommand();
            churn.CommandText = "UPDATE processing_jobs SET updated_utc = updated_utc WHERE processor_key <> 'source-text-v1'";
            churn.ExecuteNonQuery();
        }

        Assert.Equal(revision, catalog.Reconsolidation.Watermark().GraphRevision);
        Assert.Single(catalog.Graph.Atlas(new GraphQuery(Limit: 1, Cursor: first.NextCursor)).Edges);

        Assert.True(catalog.Knowledge.SetType(Single(catalog, "NodeThree").EntityId, "technology"));
        (long changed, long newest) = catalog.Reconsolidation.Watermark();
        Assert.True(changed > revision);
        Assert.True(newest >= latest);
        Assert.Throws<InvalidOperationException>(() => catalog.Graph.Atlas(new GraphQuery(Limit: 1, Cursor: first.NextCursor)));
    }
}
