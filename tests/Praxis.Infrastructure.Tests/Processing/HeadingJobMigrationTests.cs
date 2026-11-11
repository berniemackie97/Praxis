using Microsoft.Data.Sqlite;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Processing;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Processing;

public sealed class HeadingJobMigrationTests : IDisposable
{
    private const string At = "2026-11-09T00:00:00.0000000Z";
    private readonly string root = Path.Combine(TestPaths.Temp,
        "praxis-v9-" + Guid.NewGuid().ToString("N"));

    public HeadingJobMigrationTests()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void VersionEightCatalogGainsHeadingJobsMatchingRecordedOutcomes()
    {
        Guid examined = Guid.NewGuid();
        Guid superseded = Guid.NewGuid();
        Guid revoked = Guid.NewGuid();
        Guid unsupported = Guid.NewGuid();
        using (SqliteConnection connection = CreateVersionEightCatalog())
        {
            AddItem(connection, examined, "/notes/examined.md", "active", ("a", "completed"));
            Execute(connection, $"""
                INSERT INTO enrichment_runs
                    (item_id, revision, processor_key, source_digest, outcome, created_utc)
                VALUES ('{examined}', 1, 'markdown-heading-v1', '{Digest("a")}', 'abstained', '{At}')
                """);
            AddItem(connection, superseded, "/notes/updated.md", "active",
                ("b", "completed"), ("c", "completed"));
            AddItem(connection, revoked, "/notes/revoked.md", "revoked", ("d", "completed"));
            AddItem(connection, unsupported, "/notes/archive.bin", "active", ("e", "unsupported"));
        }

        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        Dictionary<(Guid ItemId, int Revision), string> headings = catalog.Processing.List()
            .Where(job => job.ProcessorKey == LocalProcessors.MarkdownHeadings)
            .ToDictionary(job => (job.ItemId, job.Revision), job => job.State);

        Assert.Equal(4, headings.Count);
        Assert.Equal("completed", headings[(examined, 1)]);
        Assert.Equal("canceled", headings[(superseded, 1)]);
        Assert.Equal("queued", headings[(superseded, 2)]);
        Assert.Equal("canceled", headings[(revoked, 1)]);
        Assert.DoesNotContain(headings.Keys, key => key.ItemId == unsupported);

        // Only the current revision is still owed a heading run.
        ProcessingDriver proposer = ProcessingDriver.Topics(catalog);
        Assert.True(proposer.ProcessNext());
        Assert.False(proposer.ProcessNext());
    }

    private SqliteConnection CreateVersionEightCatalog()
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "catalog.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
        foreach (string resource in new[] { "Catalog.V1.sql" }
            .Concat(Enumerable.Range(2, 7).Select(version => $"Catalog.V{version}.sql")))
        {
            using Stream stream = typeof(SqliteCatalog).Assembly
                .GetManifestResourceStream("Praxis.Infrastructure." + resource)!;
            using StreamReader reader = new(stream);
            Execute(connection, reader.ReadToEnd());
        }

        return connection;
    }

    private static void AddItem(
        SqliteConnection connection, Guid itemId, string locator, string status,
        params (string Seed, string Outcome)[] revisions)
    {
        Guid sourceId = Guid.NewGuid();
        Execute(connection, $"""
            INSERT INTO sources (source_id, source_kind, locator, created_utc, status)
            VALUES ('{sourceId}', 'file', '{locator}', '{At}', '{status}');
            INSERT INTO items (item_id, source_id) VALUES ('{itemId}', '{sourceId}');
            """);
        for (int index = 0; index < revisions.Length; index++)
        {
            (string seed, string outcome) = revisions[index];
            string digest = Digest(seed);
            string content = outcome == "completed" ? "'# Heading'" : "NULL";
            string reason = outcome == "completed" ? "NULL" : "'unsupported_format'";
            Execute(connection, $"""
                INSERT INTO original_objects (digest, byte_length, recorded_utc)
                VALUES ('{digest}', 9, '{At}');
                INSERT INTO item_versions (item_id, revision, digest, observed_utc)
                VALUES ('{itemId}', {index + 1}, '{digest}', '{At}');
                INSERT INTO processing_jobs
                    (item_id, revision, processor_key, state, attempt, total_attempts,
                     created_utc, updated_utc)
                VALUES ('{itemId}', {index + 1}, 'source-text-v1', '{outcome}', 1, 1, '{At}', '{At}');
                INSERT INTO text_derivatives
                    (item_id, revision, processor_key, source_digest, outcome, content,
                     reason_code, created_utc)
                VALUES ('{itemId}', {index + 1}, 'source-text-v1', '{digest}', '{outcome}',
                        {content}, {reason}, '{At}');
                """);
        }
    }

    private static string Digest(string seed) => string.Concat(Enumerable.Repeat(seed, 64));

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
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
