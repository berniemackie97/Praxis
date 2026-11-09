using System.Text;
using Microsoft.Data.Sqlite;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class TextSegmentStageTests : IDisposable
{
    private const string At = "2026-11-09T00:00:00.0000000Z";
    private readonly string workspace = Path.Combine(Path.GetTempPath(),
        "praxis-segments-" + Guid.NewGuid().ToString("N"));
    private readonly string root;

    public TextSegmentStageTests()
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
    public async Task CompletedTextIsSegmentedWithExactSpansAndFencedBySourceState()
    {
        using var catalog = LocalCatalog.Open(root);
        var body = "# Guide\n" + new string('a', 400) + "\n\n## Install\n" + new string('b', 400) + "\n";
        var first = await Capture(catalog, "guide.md", body, "first");
        await DrainAsync(catalog);

        var segments = catalog.Segments.List(first.ItemId, 1);
        Assert.Equal(2, segments.Count);
        Assert.Equal(body, string.Concat(segments.Select(segment => segment.Text)));
        Assert.Equal(["Guide", "Guide > Install"], segments.Select(segment => segment.HeadingPath));
        Assert.All(segments, segment =>
        {
            Assert.Equal(first.Sha256, segment.SourceSha256);
            Assert.Equal(body.Substring(segment.Start, segment.Length), segment.Text);
        });

        var second = await Capture(catalog, "guide.md", "# Guide\nRewritten.\n", "second");
        Assert.Empty(catalog.Segments.List(first.ItemId, 1));
        await DrainAsync(catalog);
        Assert.Equal("# Guide\nRewritten.\n", Assert.Single(catalog.Segments.List(second.ItemId, 2)).Text);

        Assert.True(catalog.Sources.Revoke(second.SourceId));
        Assert.Empty(catalog.Segments.List(second.ItemId, 2));
    }

    [Fact]
    public async Task UnsupportedTextIsNotSegmented()
    {
        using var catalog = LocalCatalog.Open(root);
        var binary = await Capture(catalog, "image.png", "\u0089PNG", "binary");
        await DrainAsync(catalog);

        Assert.DoesNotContain(catalog.Processing.List(), job =>
            job.ItemId == binary.ItemId && job.ProcessorKey == LocalProcessors.TextSegments);
    }

    [Fact]
    public async Task RenewExtendsHeldLeasesAndStopsRevokedOrSupersededWork()
    {
        using var catalog = LocalCatalog.Open(root);
        var kept = await Capture(catalog, "kept.txt", "kept", "kept");
        var revoked = await Capture(catalog, "revoked.txt", "revoked", "revoked");
        var superseded = await Capture(catalog, "superseded.txt", "old", "superseded");
        var now = DateTimeOffset.UtcNow;
        var leases = Enumerable.Range(0, 3).Select(_ => Assert.IsType<ProcessingLease>(
            catalog.Processing.TryClaim(TimeSpan.FromMinutes(1), nowUtc: now,
                processorKey: LocalProcessors.SourceText))).ToDictionary(lease => lease.ItemId);

        var renewed = Assert.IsType<ProcessingLease>(catalog.Processing.Renew(
            leases[kept.ItemId], TimeSpan.FromMinutes(10), now.AddSeconds(30)));
        Assert.Equal(now.AddSeconds(30).AddMinutes(10).UtcDateTime, renewed.LeaseUntil.UtcDateTime);
        Assert.Null(catalog.Processing.Renew(
            leases[kept.ItemId], TimeSpan.FromMinutes(1), now.AddMinutes(11)));

        Assert.True(catalog.Sources.Revoke(revoked.SourceId));
        Assert.Null(catalog.Processing.Renew(leases[revoked.ItemId], TimeSpan.FromMinutes(1), now));

        await Capture(catalog, "superseded.txt", "new", "superseded-2");
        Assert.Null(catalog.Processing.Renew(leases[superseded.ItemId], TimeSpan.FromMinutes(1), now));
        Assert.Equal("canceled", catalog.Processing.List().Single(job =>
            job.ItemId == superseded.ItemId && job.Revision == 1).State);
    }

    [Fact]
    public async Task VersionNineBackfillSegmentsNewestFirstAfterNewWork()
    {
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        using (var connection = CreateCatalog(throughVersion: 9))
        {
            AddCompletedText(connection, older, "/notes/older.txt", "a", "2026-01-01T00:00:00.0000000Z");
            AddCompletedText(connection, newer, "/notes/newer.txt", "b", "2026-06-01T00:00:00.0000000Z");
        }

        using var catalog = LocalCatalog.Open(root);
        var backfilled = catalog.Processing.List()
            .Where(job => job.ProcessorKey == LocalProcessors.TextSegments)
            .Select(job => job.ItemId).ToArray();
        Assert.Equal(new[] { older, newer }.Order(), backfilled.Order());

        var fresh = await Capture(catalog, "fresh.txt", "fresh", "fresh");
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());

        var order = new List<Guid>();
        while (catalog.Processing.TryClaim(TimeSpan.FromMinutes(1),
            processorKey: LocalProcessors.TextSegments) is { } lease)
        {
            order.Add(lease.ItemId);
        }

        Assert.Equal([fresh.ItemId, newer, older], order);
    }

    private SqliteConnection CreateCatalog(int throughVersion)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "catalog.sqlite"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        connection.Open();
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

    private static void AddCompletedText(
        SqliteConnection connection, Guid itemId, string locator, string seed, string observed)
    {
        var digest = string.Concat(Enumerable.Repeat(seed, 64));
        Execute(connection, $"""
            INSERT INTO sources (source_id, source_kind, locator, created_utc)
            VALUES ('{Guid.NewGuid()}', 'file', '{locator}', '{At}');
            INSERT INTO items (item_id, source_id)
            SELECT '{itemId}', source_id FROM sources WHERE locator = '{locator}';
            INSERT INTO original_objects (digest, byte_length, recorded_utc) VALUES ('{digest}', 4, '{At}');
            INSERT INTO item_versions (item_id, revision, digest, observed_utc)
            VALUES ('{itemId}', 1, '{digest}', '{observed}');
            INSERT INTO processing_jobs
                (item_id, revision, processor_key, state, attempt, total_attempts, created_utc, updated_utc)
            VALUES ('{itemId}', 1, 'source-text-v1', 'completed', 1, 1, '{At}', '{At}');
            INSERT INTO text_derivatives
                (item_id, revision, processor_key, source_digest, outcome, content, reason_code, created_utc)
            VALUES ('{itemId}', 1, 'source-text-v1', '{digest}', 'completed', 'text', NULL, '{At}');
            """);
    }

    private static async Task DrainAsync(LocalCatalog catalog)
    {
        var pipeline = LocalProcessingPipeline.CreateDefault(catalog);
        while (await pipeline.ProcessNextAsync())
        {
        }
    }

    private async Task<CapturedOriginal> Capture(
        LocalCatalog catalog, string filename, string contents, string key)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(contents));
        return await catalog.Originals.CaptureFileAsync(
            Path.Combine(workspace, filename), stream, 20_000, key);
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
