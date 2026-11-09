using System.Text;
using Microsoft.Data.Sqlite;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class LocalTopicProposalTests : IDisposable
{
    private readonly string workspace = Path.Combine(Path.GetTempPath(),
        "praxis-topics-" + Guid.NewGuid().ToString("N"));
    private readonly string root;

    public LocalTopicProposalTests()
    {
        root = Path.Combine(workspace, "catalog");
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(workspace, mode);
            File.SetUnixFileMode(root, mode);
        }
    }

    [Fact]
    public async Task MarkdownHeadingsAreGroundedBoundedAndReviewedWithoutEditingTheOriginal()
    {
        const string content = "# Architecture\nSystem overview\n## Runtime\n~~~csharp\n# Not a heading\n~~~\n";
        using var catalog = LocalCatalog.Open(root);
        var captured = await Capture(catalog, "topic.md", content, "one");
        Assert.True(await new LocalTextProcessor(catalog).ProcessNextAsync());
        var proposer = new LocalTopicProposer(catalog);
        Assert.True(proposer.ProcessNext());
        Assert.False(proposer.ProcessNext());

        var proposals = catalog.ListTopicProposals(captured.SourceId);
        Assert.Equal(new[] { "Architecture", "Runtime" }, proposals.Select(p => p.Value).ToArray());
        foreach (var proposal in proposals)
        {
            Assert.Equal(captured.ItemId, proposal.ItemId);
            Assert.Equal(captured.Sha256, proposal.SourceSha256);
            Assert.Equal(LocalCatalog.TopicProducer, proposal.Producer);
            Assert.Equal(proposal.Evidence, content.Substring(proposal.EvidenceOffset,
                proposal.Evidence.Length));
            Assert.Equal("pending", proposal.Status);
        }

        Assert.True(catalog.ReviewTopicProposal(proposals[0].ProposalId, "accepted"));
        Assert.False(catalog.ReviewTopicProposal(proposals[0].ProposalId, "accepted"));
        Assert.True(catalog.ReviewTopicProposal(proposals[0].ProposalId, "corrected",
            "System architecture"));
        Assert.True(catalog.ReviewTopicProposal(proposals[1].ProposalId, "rejected"));
        Assert.Equal(new[] { "corrected", "rejected" },
            catalog.ListTopicProposals(captured.SourceId).Select(p => p.Status).ToArray());
        Assert.Equal("System architecture",
            catalog.ListTopicProposals(captured.SourceId)[0].Correction);
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM enrichment_reviews"));
        await catalog.VerifyOriginalAsync(captured.ItemId);
    }

    [Fact]
    public async Task RevisedOrRevokedSourcesNeverExposeOrAcceptStaleSuggestions()
    {
        using var catalog = LocalCatalog.Open(root);
        var first = await Capture(catalog, "topic.md", "# Initial\nBody", "one");
        Assert.True(await new LocalTextProcessor(catalog).ProcessNextAsync());
        Assert.True(new LocalTopicProposer(catalog).ProcessNext());
        var old = Assert.Single(catalog.ListTopicProposals(first.SourceId));
        Assert.True(catalog.ReviewTopicProposal(old.ProposalId, "accepted"));

        var revised = await Capture(catalog, "topic.md", "# Updated\nRevised body", "two");
        Assert.Equal(first.ItemId, revised.ItemId);
        Assert.Equal(2, revised.Revision);
        Assert.Empty(catalog.ListTopicProposals(first.SourceId));
        Assert.False(catalog.ReviewTopicProposal(old.ProposalId, "rejected"));
        Assert.True(await new LocalTextProcessor(catalog).ProcessNextAsync());
        Assert.True(new LocalTopicProposer(catalog).ProcessNext());
        var current = Assert.Single(catalog.ListTopicProposals(first.SourceId));
        Assert.Equal("Updated", current.Value);
        Assert.NotEqual(old.ProposalId, current.ProposalId);
        Assert.Equal("pending", current.Status);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM enrichment_reviews"));

        Assert.True(catalog.RevokeSource(first.SourceId));
        Assert.Empty(catalog.ListTopicProposals(first.SourceId));
        Assert.False(catalog.ReviewTopicProposal(current.ProposalId, "accepted"));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM enrichment_proposals"));
        await catalog.VerifyOriginalAsync(first.ItemId);
    }

    [Fact]
    public async Task UnsupportedTextAndEmptyMarkdownAbstainWithoutInventingTopics()
    {
        using var catalog = LocalCatalog.Open(root);
        var empty = await Capture(catalog, "empty.md",
            "No heading.\n~~~\n# Fake heading\n~~~\n", "one");
        var plain = await Capture(catalog, "plain.txt", "# Document", "two");
        await Capture(catalog, "document.pdf", "# Fiction", "three");
        var processor = new LocalTextProcessor(catalog);
        Assert.True(await processor.ProcessNextAsync());
        Assert.True(await processor.ProcessNextAsync());
        Assert.True(await processor.ProcessNextAsync());
        var proposer = new LocalTopicProposer(catalog);
        Assert.True(proposer.ProcessNext());
        Assert.False(proposer.ProcessNext());
        Assert.Empty(catalog.ListTopicProposals(empty.SourceId));
        Assert.Empty(catalog.ListTopicProposals(plain.SourceId));
        Assert.Equal("abstained", Scalar(
            "SELECT outcome FROM enrichment_runs WHERE item_id = '" + empty.ItemId + "'"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM enrichment_proposals"));
    }

    [Fact]
    public async Task SeparateSourcesAndRecoveryRetainIndependentReviewHistory()
    {
        var backup = Path.Combine(workspace, "backup");
        var target = Path.Combine(workspace, "restore");
        CapturedOriginal first;
        CapturedOriginal second;
        using (var catalog = LocalCatalog.Open(root))
        {
            first = await Capture(catalog, "one.md", "# Shared Topic\nBody", "one");
            second = await Capture(catalog, "two.md", "# Shared Topic\nBody", "two");
            var processor = new LocalTextProcessor(catalog);
            Assert.True(await processor.ProcessNextAsync());
            Assert.True(await processor.ProcessNextAsync());
            var proposer = new LocalTopicProposer(catalog);
            Assert.True(proposer.ProcessNext());
            Assert.True(proposer.ProcessNext());
            var firstProposal = Assert.Single(catalog.ListTopicProposals(first.SourceId));
            var secondProposal = Assert.Single(catalog.ListTopicProposals(second.SourceId));
            Assert.NotEqual(firstProposal.ProposalId, secondProposal.ProposalId);
            Assert.True(catalog.ReviewTopicProposal(firstProposal.ProposalId, "accepted"));
            await catalog.BackupAsync(backup, 20_000_000);
        }

        await LocalCatalog.RestoreAsync(backup, target, 20_000_000);
        using var restored = LocalCatalog.Open(target);
        Assert.Equal(7, restored.SchemaVersion);
        Assert.Equal("accepted",
            Assert.Single(restored.ListTopicProposals(first.SourceId)).Status);
        Assert.Equal("pending",
            Assert.Single(restored.ListTopicProposals(second.SourceId)).Status);
        Assert.False(new LocalTopicProposer(restored).ProcessNext());
        await restored.VerifyOriginalAsync(first.ItemId);
        await restored.VerifyOriginalAsync(second.ItemId);
    }

    [Fact]
    public void ExistingVersionSixCatalogMigratesWithoutLosingUserCollections()
    {
        var databasePath = Path.Combine(root, "catalog.sqlite");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString()))
        {
            connection.Open();
            foreach (var version in new[]
                { "LocalCatalog.sql", "LocalCatalog.V2.sql", "LocalCatalog.V3.sql",
                  "LocalCatalog.V4.sql", "LocalCatalog.V5.sql", "LocalCatalog.V6.sql" })
            {
                using var resource = typeof(LocalCatalog).Assembly.GetManifestResourceStream(
                    "Praxis.Storage." + version)!;
                using var reader = new StreamReader(resource);
                using var command = connection.CreateCommand();
                command.CommandText = reader.ReadToEnd();
                command.ExecuteNonQuery();
            }

            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO source_collections
                    (collection_id, name, name_key, created_utc)
                VALUES ($id, 'Research', 'RESEARCH', '2026-11-09T00:00:00Z')
                """;
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            insert.ExecuteNonQuery();
        }

        using var upgraded = LocalCatalog.Open(root);
        Assert.Equal(7, upgraded.SchemaVersion);
        Assert.Equal("Research", Assert.Single(upgraded.ListCollections()).Name);
        Assert.False(new LocalTopicProposer(upgraded).ProcessNext());
        Assert.Empty(upgraded.ListTopicProposals(Guid.NewGuid()));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM enrichment_runs"));
    }

    [Fact]
    public async Task ReviewRejectsInvalidDecisionsCorrectionsAndUnknownSources()
    {
        using var catalog = LocalCatalog.Open(root);
        var item = await Capture(catalog, "notes.md", "# Testable\nBody", "one");
        Assert.True(await new LocalTextProcessor(catalog).ProcessNextAsync());
        Assert.True(new LocalTopicProposer(catalog).ProcessNext());
        var candidate = Assert.Single(catalog.ListTopicProposals(item.SourceId));
        Assert.Throws<ArgumentException>(() =>
            catalog.ReviewTopicProposal(candidate.ProposalId, "approved"));
        Assert.Throws<ArgumentException>(() =>
            catalog.ReviewTopicProposal(candidate.ProposalId, "corrected", " "));
        Assert.Throws<ArgumentException>(() =>
            catalog.ReviewTopicProposal(candidate.ProposalId, "accepted", "unexpected"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            catalog.ListTopicProposals(item.SourceId, 101));
        Assert.False(catalog.ReviewTopicProposal(Guid.NewGuid(), "accepted"));
        Assert.Equal("pending", Assert.Single(catalog.ListTopicProposals(item.SourceId)).Status);
    }

    private async Task<CapturedOriginal> Capture(
        LocalCatalog catalog, string filename, string contents, string key)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(contents));
        return await catalog.CaptureFileAsync(
            Path.Combine(workspace, filename), stream, 20_000, key);
    }

    private object? Scalar(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "catalog.sqlite"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, true);
        }
    }
}
