using Microsoft.Data.Sqlite;
using Praxis.Storage;
using Praxis.Storage.Catalog;
using Praxis.Storage.Processing;
using Xunit;

namespace Praxis.Storage.Tests.Enrichment;

public sealed class ProposalSchemaMigrationTests : IDisposable
{
    private const string At = "2026-11-09T00:00:00.0000000Z";
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "praxis-v12-" + Guid.NewGuid().ToString("N"));

    public ProposalSchemaMigrationTests()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void VersionElevenProposalsAndReviewsSurviveTheRebuildAndSegmentsAreQueued()
    {
        var item = Guid.NewGuid();
        var proposal = Guid.NewGuid();
        var source = Guid.NewGuid();
        using (var connection = CreateCatalog(throughVersion: 11))
        {
            Seed(connection, item, source, proposal);
            Execute(connection, $"""
                INSERT INTO enrichment_reviews (review_id, proposal_id, decision, correction, reviewed_utc)
                VALUES ('{Guid.NewGuid()}', '{proposal}', 'corrected', 'Architecture overview', '{At}');
                """);
        }

        using var catalog = LocalCatalog.Open(root);

        var migrated = Assert.Single(catalog.Proposals.List(source));
        Assert.Equal(proposal, migrated.ProposalId);
        Assert.Equal("topic", migrated.Kind);
        Assert.Null(migrated.EntityType);
        Assert.Equal("corrected", migrated.Status);
        Assert.Equal("Architecture overview", migrated.Correction);
        Assert.Equal("queued", catalog.Processing.List().Single(job =>
            job.ProcessorKey == LocalProcessors.SemanticExtraction).State);

        Assert.True(catalog.Proposals.Review(proposal, "corrected", new string('c', 120)));
        Assert.Throws<ArgumentException>(() => catalog.Proposals.Review(proposal, "corrected", new string('c', 121)));
    }

    [Fact]
    public void VersionTwelveSemanticResultsStayVisibleUntilVersionTwoReplacesThem()
    {
        var item = Guid.NewGuid();
        var source = Guid.NewGuid();
        var digest = new string('a', 64);
        using (var connection = CreateCatalog(throughVersion: 12))
        {
            Seed(connection, item, source, Guid.NewGuid(), version: 12);
            Execute(connection, $"""
                INSERT INTO processing_jobs
                    (item_id, revision, processor_key, state, attempt, total_attempts, created_utc, updated_utc)
                VALUES ('{item}', 1, 'semantic-extract-v1', 'completed', 1, 1, '{At}', '{At}');
                INSERT INTO enrichment_runs (item_id, revision, processor_key, source_digest, outcome, created_utc)
                VALUES ('{item}', 1, 'semantic-extract-v1', '{digest}', 'generated', '{At}');
                INSERT INTO enrichment_proposals
                    (proposal_id, item_id, revision, processor_key, source_digest, kind, entity_type, value,
                     evidence, evidence_offset, evidence_length, status, created_utc)
                VALUES ('{Guid.NewGuid()}', '{item}', 1, 'semantic-extract-v1', '{digest}', 'entity',
                        'concept', 'Architecture', 'Architecture', 2, 12, 'pending', '{At}');
                """);
        }

        using (var catalog = LocalCatalog.Open(root))
        {
            Assert.Contains(catalog.Proposals.List(source), proposal => proposal.Producer == "semantic-extract-v1");
            var jobs = catalog.Processing.List().Where(job => job.ItemId == item).ToArray();
            Assert.Equal("queued", jobs.Single(job => job.ProcessorKey == LocalProcessors.SemanticExtraction).State);
            Assert.Equal("completed", jobs.Single(job => job.ProcessorKey == "semantic-extract-v1").State);
        }

        using (var connection = Connect())
        {
            Execute(connection, $"""
                INSERT INTO enrichment_runs (item_id, revision, processor_key, source_digest, outcome, created_utc)
                VALUES ('{item}', 1, 'semantic-extract-v2', '{digest}', 'abstained', '{At}');
                """);
        }

        using var replaced = LocalCatalog.Open(root);
        Assert.DoesNotContain(replaced.Proposals.List(source), proposal => proposal.Producer == "semantic-extract-v1");
        Assert.Contains(replaced.Proposals.List(source), proposal => proposal.Producer == LocalProcessors.MarkdownHeadings);
    }

    [Fact]
    public void OrphanedReferencesAreRejectedBeforeAnyUpgrade()
    {
        using (var connection = CreateCatalog(throughVersion: 11))
        {
            Execute(connection, $"""
                PRAGMA foreign_keys = OFF;
                INSERT INTO enrichment_reviews (review_id, proposal_id, decision, correction, reviewed_utc)
                VALUES ('{Guid.NewGuid()}', '{Guid.NewGuid()}', 'accepted', NULL, '{At}');
                """);
        }

        Assert.Throws<InvalidDataException>(() => LocalCatalog.Open(root));

        using var check = Connect();
        Assert.Equal(11L, Scalar(check, "PRAGMA user_version"));
        Assert.Equal(0L, Scalar(check,
            "SELECT COUNT(*) FROM pragma_table_info('enrichment_proposals') WHERE name = 'entity_type'"));
    }

    private static void Seed(SqliteConnection connection, Guid item, Guid source, Guid proposal, int version = 11)
    {
        var lengthColumn = version >= 12 ? ", evidence_length" : "";
        var lengthValue = version >= 12 ? ", 12" : "";
        var digest = new string('a', 64);
        Execute(connection, $"""
            INSERT INTO sources (source_id, source_kind, locator, created_utc)
            VALUES ('{source}', 'file', '/notes/plan.md', '{At}');
            INSERT INTO items (item_id, source_id) VALUES ('{item}', '{source}');
            INSERT INTO original_objects (digest, byte_length, recorded_utc) VALUES ('{digest}', 20, '{At}');
            INSERT INTO item_versions (item_id, revision, digest, observed_utc)
            VALUES ('{item}', 1, '{digest}', '{At}');
            INSERT INTO processing_jobs
                (item_id, revision, processor_key, state, attempt, total_attempts, created_utc, updated_utc)
            VALUES ('{item}', 1, 'source-text-v1', 'completed', 1, 1, '{At}', '{At}'),
                   ('{item}', 1, 'markdown-heading-v1', 'completed', 1, 1, '{At}', '{At}'),
                   ('{item}', 1, 'text-segments-v1', 'completed', 1, 1, '{At}', '{At}');
            INSERT INTO text_derivatives
                (item_id, revision, processor_key, source_digest, outcome, content, reason_code, created_utc)
            VALUES ('{item}', 1, 'source-text-v1', '{digest}', 'completed', '# Architecture\nBody', NULL, '{At}');
            INSERT INTO enrichment_runs (item_id, revision, processor_key, source_digest, outcome, created_utc)
            VALUES ('{item}', 1, 'markdown-heading-v1', '{digest}', 'generated', '{At}');
            INSERT INTO enrichment_proposals
                (proposal_id, item_id, revision, processor_key, source_digest, kind, value, evidence,
                 evidence_offset, status, correction, created_utc, reviewed_utc{lengthColumn})
            VALUES ('{proposal}', '{item}', 1, 'markdown-heading-v1', '{digest}', 'topic', 'Architecture',
                    'Architecture', 2, 'corrected', 'Architecture overview', '{At}', '{At}'{lengthValue});
            """);
    }

    private SqliteConnection CreateCatalog(int throughVersion)
    {
        var connection = Connect();
        foreach (var resource in new[] { "LocalCatalog.sql" }
            .Concat(Enumerable.Range(2, throughVersion - 1).Select(version => $"LocalCatalog.V{version}.sql")))
        {
            using var stream = typeof(LocalCatalog).Assembly
                .GetManifestResourceStream("Praxis.Storage." + resource)!;
            using var reader = new StreamReader(stream);
            Execute(connection, reader.ReadToEnd());
        }

        return connection;
    }

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "catalog.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
