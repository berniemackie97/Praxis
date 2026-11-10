using System.Text;
using Microsoft.Data.Sqlite;
using Praxis.Storage.Catalog;
using Praxis.Storage.Enrichment;
using Praxis.Storage.Originals;
using Praxis.Storage.Processing;
using Praxis.Storage.Recovery;
using Praxis.Storage.Tests.Support;
using Xunit;

namespace Praxis.Storage.Tests.Enrichment;

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
            UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(workspace, mode);
            File.SetUnixFileMode(root, mode);
        }
    }

    [Fact]
    public async Task MarkdownHeadingsAreGroundedBoundedAndReviewedWithoutEditingTheOriginal()
    {
        const string content = "# Architecture\nSystem overview\n## Runtime\n~~~csharp\n# Not a heading\n~~~\n";
        using LocalCatalog catalog = LocalCatalog.Open(root);
        CapturedOriginal captured = await Capture(catalog, "topic.md", content, "one");
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        ProcessingDriver proposer = ProcessingDriver.Topics(catalog);
        Assert.True(proposer.ProcessNext());
        Assert.False(proposer.ProcessNext());
        Assert.Equal("generated", catalog.Topics.Outcome(captured.SourceId));

        IReadOnlyList<LocalTopicProposal> proposals = catalog.Topics.List(captured.SourceId);
        Assert.Equal(new[] { "Architecture", "Runtime" }, proposals.Select(p => p.Value).ToArray());
        foreach (LocalTopicProposal proposal in proposals)
        {
            Assert.Equal(captured.ItemId, proposal.ItemId);
            Assert.Equal(captured.Sha256, proposal.SourceSha256);
            Assert.Equal(LocalProcessors.MarkdownHeadings, proposal.Producer);
            Assert.Equal(proposal.Evidence, content.Substring(proposal.EvidenceOffset,
                proposal.Evidence.Length));
            Assert.Equal("pending", proposal.Status);
        }

        Assert.True(catalog.Topics.Review(proposals[0].ProposalId, "accepted"));
        Assert.False(catalog.Topics.Review(proposals[0].ProposalId, "accepted"));
        Assert.True(catalog.Topics.Review(proposals[0].ProposalId, "corrected",
            "System architecture"));
        Assert.True(catalog.Topics.Review(proposals[1].ProposalId, "rejected"));
        Assert.Equal(new[] { "corrected", "rejected" },
            catalog.Topics.List(captured.SourceId).Select(p => p.Status).ToArray());
        Assert.Equal("System architecture",
            catalog.Topics.List(captured.SourceId)[0].Correction);
        Assert.Equal(3L, Scalar("SELECT COUNT(*) FROM enrichment_reviews"));
        await catalog.Originals.VerifyAsync(captured.ItemId);
    }

    [Fact]
    public async Task RevisedOrRevokedSourcesNeverExposeOrAcceptStaleSuggestions()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        CapturedOriginal first = await Capture(catalog, "topic.md", "# Initial\nBody", "one");
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        Assert.True(ProcessingDriver.Topics(catalog).ProcessNext());
        LocalTopicProposal old = Assert.Single(catalog.Topics.List(first.SourceId));
        Assert.True(catalog.Topics.Review(old.ProposalId, "accepted"));

        CapturedOriginal revised = await Capture(catalog, "topic.md", "# Updated\nRevised body", "two");
        Assert.Equal(first.ItemId, revised.ItemId);
        Assert.Equal(2, revised.Revision);
        Assert.Empty(catalog.Topics.List(first.SourceId));
        Assert.Equal("pending", catalog.Topics.Outcome(first.SourceId));
        Assert.False(catalog.Topics.Review(old.ProposalId, "rejected"));
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        Assert.True(ProcessingDriver.Topics(catalog).ProcessNext());
        LocalTopicProposal current = Assert.Single(catalog.Topics.List(first.SourceId));
        Assert.Equal("Updated", current.Value);
        Assert.NotEqual(old.ProposalId, current.ProposalId);
        Assert.Equal("pending", current.Status);
        Assert.Equal(1L, Scalar("SELECT COUNT(*) FROM enrichment_reviews"));

        Assert.True(catalog.Sources.Revoke(first.SourceId));
        Assert.Equal("unavailable", catalog.Topics.Outcome(first.SourceId));
        Assert.Empty(catalog.Topics.List(first.SourceId));
        Assert.False(catalog.Topics.Review(current.ProposalId, "accepted"));
        Assert.Equal(2L, Scalar("SELECT COUNT(*) FROM enrichment_proposals"));
        await catalog.Originals.VerifyAsync(first.ItemId);
    }

    [Fact]
    public async Task RevocationBetweenReadAndCommitPublishesNoProposals()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        CapturedOriginal source = await Capture(catalog, "fenced.md", "# Fenced topic\n", "fenced");
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        ProcessingLease lease = Assert.IsType<ProcessingLease>(catalog.Processing.TryClaim(
            TimeSpan.FromMinutes(1), processorKey: LocalProcessors.MarkdownHeadings));
        TopicCandidate candidate = Assert.IsType<TopicCandidate>(catalog.Topics.ReadLeasedCandidate(lease));

        Assert.True(catalog.Sources.Revoke(source.SourceId));

        Assert.False(catalog.Topics.CommitCandidates(
            lease, candidate, [new TopicEvidence("Fenced topic", 2)]));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM enrichment_runs"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM enrichment_proposals"));
        Assert.Equal("canceled", Scalar(
            "SELECT state FROM processing_jobs WHERE processor_key = 'markdown-heading-v1'"));
    }

    [Fact]
    public async Task UnsupportedTextAndEmptyMarkdownAbstainWithoutInventingTopics()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        CapturedOriginal empty = await Capture(catalog, "empty.md",
            "No heading.\n~~~\n# Fake heading\n~~~\n", "one");
        CapturedOriginal plain = await Capture(catalog, "plain.txt", "# Document", "two");
        await Capture(catalog, "document.pdf", "# Fiction", "three");
        ProcessingDriver processor = ProcessingDriver.Text(catalog);
        Assert.True(await processor.ProcessNextAsync());
        Assert.True(await processor.ProcessNextAsync());
        Assert.True(await processor.ProcessNextAsync());
        // Heading jobs exist only for completed extractions: the Markdown and the text file,
        // not the unsupported PDF.
        ProcessingDriver proposer = ProcessingDriver.Topics(catalog);
        Assert.True(proposer.ProcessNext());
        Assert.True(proposer.ProcessNext());
        Assert.False(proposer.ProcessNext());
        Assert.Empty(catalog.Topics.List(empty.SourceId));
        Assert.Empty(catalog.Topics.List(plain.SourceId));
        Assert.Equal("abstained", catalog.Topics.Outcome(empty.SourceId));
        Assert.Equal("unsupported", catalog.Topics.Outcome(plain.SourceId));
        Assert.Equal("abstained", Scalar(
            "SELECT outcome FROM enrichment_runs WHERE item_id = '" + empty.ItemId + "'"));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM enrichment_proposals"));
    }

    [Fact]
    public async Task SeparateSourcesAndRecoveryRetainIndependentReviewHistory()
    {
        string backup = Path.Combine(workspace, "backup");
        string target = Path.Combine(workspace, "restore");
        CapturedOriginal first;
        CapturedOriginal second;
        using (LocalCatalog catalog = LocalCatalog.Open(root))
        {
            first = await Capture(catalog, "one.md", "# Shared Topic\nBody", "one");
            second = await Capture(catalog, "two.md", "# Shared Topic\nBody", "two");
            ProcessingDriver processor = ProcessingDriver.Text(catalog);
            Assert.True(await processor.ProcessNextAsync());
            Assert.True(await processor.ProcessNextAsync());
            ProcessingDriver proposer = ProcessingDriver.Topics(catalog);
            Assert.True(proposer.ProcessNext());
            Assert.True(proposer.ProcessNext());
            LocalTopicProposal firstProposal = Assert.Single(catalog.Topics.List(first.SourceId));
            LocalTopicProposal secondProposal = Assert.Single(catalog.Topics.List(second.SourceId));
            Assert.NotEqual(firstProposal.ProposalId, secondProposal.ProposalId);
            Assert.True(catalog.Topics.Review(firstProposal.ProposalId, "accepted"));
            await catalog.Recovery.BackupAsync(backup, 20_000_000);
        }

        await LocalCatalogSnapshots.RestoreAsync(backup, target, 20_000_000);
        using LocalCatalog restored = LocalCatalog.Open(target);
        Assert.Equal(9, restored.SchemaVersion);
        Assert.Equal("accepted",
            Assert.Single(restored.Topics.List(first.SourceId)).Status);
        Assert.Equal("pending",
            Assert.Single(restored.Topics.List(second.SourceId)).Status);
        Assert.False(ProcessingDriver.Topics(restored).ProcessNext());
        await restored.Originals.VerifyAsync(first.ItemId);
        await restored.Originals.VerifyAsync(second.ItemId);
    }

    [Fact]
    public void ExistingVersionSixCatalogMigratesWithoutLosingUserCollections()
    {
        string databasePath = Path.Combine(root, "catalog.sqlite");
        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString()))
        {
            connection.Open();
            foreach (string version in new[]
                { "LocalCatalog.sql", "LocalCatalog.V2.sql", "LocalCatalog.V3.sql",
                  "LocalCatalog.V4.sql", "LocalCatalog.V5.sql", "LocalCatalog.V6.sql" })
            {
                using Stream resource = typeof(LocalCatalog).Assembly.GetManifestResourceStream(
                    "Praxis.Storage." + version)!;
                using StreamReader reader = new(resource);
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = reader.ReadToEnd();
                command.ExecuteNonQuery();
            }

            using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO source_collections
                    (collection_id, name, name_key, created_utc)
                VALUES ($id, 'Research', 'RESEARCH', '2026-11-09T00:00:00Z')
                """;
            insert.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            insert.ExecuteNonQuery();
        }

        using LocalCatalog upgraded = LocalCatalog.Open(root);
        Assert.Equal(9, upgraded.SchemaVersion);
        Assert.Equal("Research", Assert.Single(upgraded.Collections.List()).Name);
        Assert.False(ProcessingDriver.Topics(upgraded).ProcessNext());
        Assert.Empty(upgraded.Topics.List(Guid.NewGuid()));
        Assert.Equal(0L, Scalar("SELECT COUNT(*) FROM enrichment_runs"));
    }

    [Fact]
    public void MissingEligibilityViewIsRejectedWithoutRewritingTheCatalog()
    {
        using (LocalCatalog catalog = LocalCatalog.Open(root)) { }
        using (SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "catalog.sqlite"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString()))
        {
            connection.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DROP VIEW eligible_enrichment_proposals";
            command.ExecuteNonQuery();
        }

        Assert.Throws<InvalidDataException>(() => LocalCatalog.Open(root));
        Assert.Equal(0L, Scalar(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='view' AND name='eligible_enrichment_proposals'"));
    }

    [Fact]
    public async Task ReviewRejectsInvalidDecisionsCorrectionsAndUnknownSources()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        CapturedOriginal item = await Capture(catalog, "notes.md", "# Testable\nBody", "one");
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        Assert.True(ProcessingDriver.Topics(catalog).ProcessNext());
        LocalTopicProposal candidate = Assert.Single(catalog.Topics.List(item.SourceId));
        Assert.Throws<ArgumentException>(() =>
            catalog.Topics.Review(candidate.ProposalId, "approved"));
        Assert.Throws<ArgumentException>(() =>
            catalog.Topics.Review(candidate.ProposalId, "corrected", " "));
        Assert.Throws<ArgumentException>(() =>
            catalog.Topics.Review(candidate.ProposalId, "accepted", "unexpected"));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            catalog.Topics.List(item.SourceId, 101));
        Assert.False(catalog.Topics.Review(Guid.NewGuid(), "accepted"));
        Assert.Equal("pending", Assert.Single(catalog.Topics.List(item.SourceId)).Status);
    }

    private async Task<CapturedOriginal> Capture(
        LocalCatalog catalog, string filename, string contents, string key)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(contents));
        return await catalog.Originals.CaptureFileAsync(
            Path.Combine(workspace, filename), stream, 20_000, key);
    }

    private object? Scalar(string sql)
    {
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "catalog.sqlite"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
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
