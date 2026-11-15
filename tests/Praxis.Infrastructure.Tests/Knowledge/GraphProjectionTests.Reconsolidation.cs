using System.Text.Json;
using Microsoft.Data.Sqlite;
using Praxis.Application.Desktop;
using Praxis.Application.Knowledge;
using Praxis.Domain.Knowledge;
using Praxis.Domain.Originals;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Knowledge;

public sealed partial class GraphProjectionTests
{
    [Fact]
    public async Task RevokingASourceRefreshesTheTypeTheRemainingEvidenceSupports()
    {
        using SqliteCatalog catalog = Open();
        types["Widget"] = "project";
        await Enrich(catalog, "product.md", "Widget is our product.");
        types["Widget"] = "technology";
        CapturedOriginal second = await Enrich(catalog, "stack-one.md", "We build on Widget.");
        CapturedOriginal third = await Enrich(catalog, "stack-two.md", "Widget again.");
        Drain(catalog);
        Assert.Equal("technology", Single(catalog, "Widget").EntityType);

        Assert.True(catalog.Sources.Revoke(second.SourceId));
        Assert.True(catalog.Sources.Revoke(third.SourceId));
        ReconsolidationPass pass = catalog.Reconsolidation.RunPass(50);

        Assert.True(pass.Applied >= 1);
        Assert.Equal("project", Single(catalog, "Widget").EntityType);
        KnowledgeRevision revision = Assert.Single(catalog.Reconsolidation.Revisions(RevisionVocabulary.Applied, 10),
            item => item.EntityName == "Widget");
        Assert.Equal(("technology", "project", RevisionVocabulary.Revocation), (revision.Before, revision.After, revision.Trigger));
        Assert.Contains("revoked", revision.Basis, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARuleChangeReexaminesJudgmentsOlderVersionsStored()
    {
        using SqliteCatalog catalog = Open();
        types["AUTH-001B"] = "project";
        await Enrich(catalog, "ticket.md", "Client ships AUTH-001B.");
        Drain(catalog);
        Execute(catalog, "UPDATE knowledge_entities SET entity_type = 'project' WHERE display_name = 'AUTH-001B'");
        Execute(catalog, "DELETE FROM runtime_settings WHERE key = 'knowledge_rules_version'");

        Assert.True(catalog.Reconsolidation.EnqueueForRuleChange() > 0);
        Assert.Equal(0, catalog.Reconsolidation.EnqueueForRuleChange());
        Drain(catalog);

        KnowledgeRevision revision = Assert.Single(catalog.Reconsolidation.Revisions(RevisionVocabulary.Applied, 10),
            item => item.EntityName == "AUTH-001B");
        Assert.Equal(("project", "other", RevisionVocabulary.Rules), (revision.Before, revision.After, revision.Trigger));
        Assert.Contains("ticket", revision.Basis, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RelationsOnlyCodeCanHaveProposeACodeTypeForTheOwnerToAccept()
    {
        using SqliteCatalog catalog = Open();
        types["Widget"] = "project";
        types["Parser"] = "function";
        types["Lexer"] = "function";
        CapturedOriginal source = await Enrich(catalog, "code.md", "Widget Parser Lexer.");
        await Accept(catalog, source);
        Drain(catalog);
        Guid widget = Single(catalog, "Widget").EntityId;
        catalog.Knowledge.Relate(widget, "calls", Single(catalog, "Parser").EntityId);
        catalog.Knowledge.Relate(Single(catalog, "Lexer").EntityId, "calls", widget);
        Drain(catalog);

        KnowledgeRevision proposal = Assert.Single(catalog.Reconsolidation.Revisions(RevisionVocabulary.Suggested, 10));
        Assert.Equal(("Widget", "project", "function", RevisionVocabulary.Relations),
            (proposal.EntityName, proposal.Before, proposal.After, proposal.Trigger));
        Assert.Equal("project", Single(catalog, "Widget").EntityType);

        DesktopProtocol desktop = TestDesktop.Protocol(catalog);
        using JsonDocument listed = JsonDocument.Parse(await desktop.HandleAsync(
            """{"requestId":"list","action":"knowledgeRevisions","state":"suggested"}"""));
        Assert.Equal(1, listed.RootElement.GetProperty("data").GetProperty("revisions").GetArrayLength());
        using JsonDocument decided = JsonDocument.Parse(await desktop.HandleAsync(
            $$"""{"requestId":"decide","action":"knowledgeRevisionDecide","revisionId":{{proposal.RevisionId}},"decision":"accepted"}"""));
        Assert.True(decided.RootElement.GetProperty("data").GetProperty("changed").GetBoolean());

        EntitySummary accepted = Single(catalog, "Widget");
        Assert.Equal(("function", "owner"), (accepted.EntityType, accepted.TypeOrigin));
        Assert.Equal(RevisionVocabulary.Accepted, catalog.Reconsolidation.Revision(proposal.RevisionId)!.State);
    }

    [Fact]
    public async Task AnOwnerCorrectionTeachesLookAlikesThatPlayTheSameRole()
    {
        using SqliteCatalog catalog = Open();
        types["CGameMap"] = "project";
        types["CHeroMap"] = "project";
        types["CLoneMap"] = "project";
        types["MapReader"] = "function";
        CapturedOriginal source = await Enrich(catalog, "classes.md", "CGameMap CHeroMap CLoneMap MapReader.");
        await Accept(catalog, source);
        Guid reader = Single(catalog, "MapReader").EntityId;
        catalog.Knowledge.Relate(Single(catalog, "CGameMap").EntityId, "uses", reader);
        catalog.Knowledge.Relate(Single(catalog, "CHeroMap").EntityId, "uses", reader);
        catalog.Knowledge.Relate(reader, "uses", Single(catalog, "CLoneMap").EntityId);
        Drain(catalog);

        Assert.True(catalog.Knowledge.SetType(Single(catalog, "CGameMap").EntityId, "class"));
        Drain(catalog);

        KnowledgeRevision lesson = Assert.Single(catalog.Reconsolidation.Revisions(RevisionVocabulary.Suggested, 10));
        Assert.Equal(("CHeroMap", "project", "class", RevisionVocabulary.OwnerLesson),
            (lesson.EntityName, lesson.Before, lesson.After, lesson.Trigger));
        Assert.Contains("CGameMap", lesson.Basis, StringComparison.Ordinal);

        RevisionReview review = new(catalog.Reconsolidation, catalog.Knowledge);
        Assert.True(review.Decide(lesson.RevisionId, accept: false));
        Assert.False(review.Decide(lesson.RevisionId, accept: true));
        // The same lesson again (project -> class) must not resurrect the dismissed proposal.
        Assert.True(catalog.Knowledge.SetType(Single(catalog, "CGameMap").EntityId, "project"));
        Drain(catalog);
        Assert.True(catalog.Knowledge.SetType(Single(catalog, "CGameMap").EntityId, "class"));
        Drain(catalog);
        Assert.Empty(catalog.Reconsolidation.Revisions(RevisionVocabulary.Suggested, 10));
        Assert.Equal("project", Single(catalog, "CHeroMap").EntityType);
    }

    [Fact]
    public async Task RevokingASourceCancelsItsWaitingProcessing()
    {
        using SqliteCatalog catalog = Open();
        using MemoryStream content = new("waiting"u8.ToArray());
        CapturedOriginal captured = await catalog.Originals.CaptureFileAsync(
            Path.Combine(workspace, "waiting.md"), content, 1_000, "waiting");
        Assert.True(Scalar(catalog, "SELECT COUNT(*) FROM processing_jobs WHERE state = 'queued'") > 0);

        Assert.True(catalog.Sources.Revoke(captured.SourceId));

        Assert.Equal(0, Scalar(catalog, "SELECT COUNT(*) FROM processing_jobs WHERE state = 'queued'"));
    }

    private static void Drain(SqliteCatalog catalog)
    {
        while (catalog.Reconsolidation.RunPass(500).Remaining > 0)
        {
        }
    }

    private static void Execute(SqliteCatalog catalog, string sql)
    {
        using SqliteConnection connection = new($"Data Source={catalog.DatabasePath}");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static long Scalar(SqliteCatalog catalog, string sql)
    {
        using SqliteConnection connection = new($"Data Source={catalog.DatabasePath};Mode=ReadOnly");
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}
