using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class ProcessingLifecycleTests : IDisposable
{
    private readonly string workspace = Path.Combine(
        Path.GetTempPath(), "praxis-processing-" + Guid.NewGuid().ToString("N"));
    private readonly string root;

    public ProcessingLifecycleTests()
    {
        root = Path.Combine(workspace, "data");
        Directory.CreateDirectory(root);
        Private(workspace);
        Private(root);
    }

    [Fact]
    public async Task NewRevisionQueuesOneJobAndCommittedReplayDoesNotDuplicateIt()
    {
        using var catalog = LocalCatalog.Open(root);
        using var original = new MemoryStream([1]);
        var first = await catalog.CaptureFileAsync(Source("a"), original, 10, "first");
        Assert.Equal("queued", Assert.Single(catalog.ListProcessingJobs()).State);
        using var unchanged = new MemoryStream([1]);
        var repeat = await catalog.CaptureFileAsync(Source("a"), unchanged, 10, "second-key");
        Assert.Equal(first, repeat);
        Assert.Single(catalog.ListProcessingJobs());

        using var revision = new MemoryStream([2]);
        var next = await catalog.CaptureFileAsync(Source("a"), revision, 10, "third-key");
        Assert.Equal(first.ItemId, next.ItemId);
        Assert.Equal(first.SourceId, next.SourceId);
        Assert.Equal(2, next.Revision);
        Assert.Equal(2, catalog.ListProcessingJobs().Count);

        Assert.False(catalog.QueueProcessing(next.ItemId, next.Revision,
            LocalCatalog.DefaultProcessorKey));
        Assert.True(catalog.QueueProcessing(next.ItemId, next.Revision, "another-v2"));
        Assert.Equal(3, catalog.ListProcessingJobs().Count);
        Assert.False(catalog.QueueProcessing(Guid.NewGuid(), 1, "another-v2"));
    }

    [Fact]
    public async Task ClaimedJobSurvivesRestartAndExpiredLeaseIsFenced()
    {
        var time = new DateTimeOffset(2026, 11, 8, 23, 0, 0, TimeSpan.Zero);
        ProcessingLease first;
        using (var catalog = LocalCatalog.Open(root))
        {
            using var data = new MemoryStream([1, 2]);
            await catalog.CaptureFileAsync(Source("a"), data, 100, "submit");
            first = Assert.IsType<ProcessingLease>(
                catalog.TryClaimProcessing(TimeSpan.FromSeconds(10), nowUtc: time));
            Assert.Equal(1, Assert.Single(catalog.ListProcessingJobs()).Attempt);
            Assert.Null(catalog.TryClaimProcessing(TimeSpan.FromSeconds(10),
                nowUtc: time.AddSeconds(5)));
        }

        using var reopened = LocalCatalog.Open(root);
        var second = Assert.IsType<ProcessingLease>(reopened.TryClaimProcessing(
            TimeSpan.FromSeconds(10), nowUtc: time.AddSeconds(11)));
        Assert.NotEqual(first.LeaseKey, second.LeaseKey);
        Assert.False(reopened.FinishProcessing(first, ProcessingCompletion.Completed,
            nowUtc: time.AddSeconds(12)));
        Assert.False(reopened.FinishProcessing(second, ProcessingCompletion.Completed,
            nowUtc: time.AddSeconds(21)));
        Assert.True(reopened.FinishProcessing(second, ProcessingCompletion.Unsupported,
            nowUtc: time.AddSeconds(12)));
        var job = Assert.Single(reopened.ListProcessingJobs());
        Assert.Equal("unsupported", job.State);
        Assert.Equal(2, job.TotalAttempts);
        Assert.Null(job.LeaseUntil);
        Assert.Null(reopened.TryClaimProcessing(TimeSpan.FromSeconds(1),
            nowUtc: time.AddMinutes(1)));
    }

    [Fact]
    public async Task AttemptExhaustionRequiresExplicitRetryAndPreservesTotalAttempts()
    {
        using var catalog = LocalCatalog.Open(root);
        using var content = new MemoryStream([2]);
        var saved = await catalog.CaptureFileAsync(Source("b"), content, 10, "submit");
        var time = new DateTimeOffset(2026, 11, 8, 23, 0, 0, TimeSpan.Zero);
        var first = Assert.IsType<ProcessingLease>(catalog.TryClaimProcessing(
            TimeSpan.FromSeconds(1), maxAttempts: 2, nowUtc: time));
        var second = Assert.IsType<ProcessingLease>(catalog.TryClaimProcessing(
            TimeSpan.FromSeconds(1), maxAttempts: 2, nowUtc: time.AddSeconds(2)));
        Assert.NotEqual(first.LeaseKey, second.LeaseKey);
        Assert.Null(catalog.TryClaimProcessing(TimeSpan.FromSeconds(1),
            maxAttempts: 2, nowUtc: time.AddSeconds(4)));
        var failed = Assert.Single(catalog.ListProcessingJobs());
        Assert.Equal("failed", failed.State);
        Assert.Equal("attempt_limit", failed.FailureCode);
        Assert.Equal(2, failed.TotalAttempts);
        Assert.True(catalog.RetryProcessing(saved.ItemId, 1, LocalCatalog.DefaultProcessorKey));
        Assert.False(catalog.RetryProcessing(saved.ItemId, 1, LocalCatalog.DefaultProcessorKey));
        var next = Assert.IsType<ProcessingLease>(catalog.TryClaimProcessing(
            TimeSpan.FromSeconds(2), maxAttempts: 2, nowUtc: time.AddSeconds(5)));
        Assert.True(catalog.FinishProcessing(next, ProcessingCompletion.Failed,
            "invalid_content", nowUtc: time.AddSeconds(6)));
        var job = Assert.Single(catalog.ListProcessingJobs());
        Assert.Equal(3, job.TotalAttempts);
        Assert.Equal("invalid_content", job.FailureCode);
        Assert.Throws<ArgumentException>(() => catalog.FinishProcessing(
            next, ProcessingCompletion.Failed, "/private/path"));
    }

    [Fact]
    public async Task CancellationAndRevocationFenceOutstandingClaimsWithoutDeletingOriginals()
    {
        using var catalog = LocalCatalog.Open(root);
        using var content = new MemoryStream([3]);
        var saved = await catalog.CaptureFileAsync(Source("c"), content, 10, "submit");
        var lease = Assert.IsType<ProcessingLease>(catalog.TryClaimProcessing(
            TimeSpan.FromHours(1)));
        Assert.True(catalog.CancelProcessing(saved.ItemId, 1, LocalCatalog.DefaultProcessorKey));
        Assert.False(catalog.FinishProcessing(lease, ProcessingCompletion.Completed));
        Assert.True(catalog.RetryProcessing(saved.ItemId, 1, LocalCatalog.DefaultProcessorKey));
        Assert.True(catalog.RevokeSource(saved.SourceId));
        Assert.False(catalog.RevokeSource(saved.SourceId));
        Assert.False(catalog.RetryProcessing(saved.ItemId, 1, LocalCatalog.DefaultProcessorKey));
        Assert.False(catalog.QueueProcessing(saved.ItemId, 1, "some-other-v1"));
        Assert.Equal("canceled", Assert.Single(catalog.ListProcessingJobs()).State);
        await catalog.VerifyOriginalAsync(saved.ItemId);
        using var retry = new MemoryStream([4]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.CaptureFileAsync(Source("c"), retry, 10, "different"));
    }

    [Fact]
    public async Task RestoreKeepsJobStateWithoutAutomaticallyExecutingJobs()
    {
        var backup = Path.Combine(workspace, "backup");
        var restored = Path.Combine(workspace, "restored");
        using (var catalog = LocalCatalog.Open(root))
        {
            using var content = new MemoryStream([7]);
            var saved = await catalog.CaptureFileAsync(Source("a"), content, 10, "one");
            Assert.True(catalog.CancelProcessing(saved.ItemId, 1,
                LocalCatalog.DefaultProcessorKey));
            await catalog.BackupAsync(backup, 10_000_000);
        }

        await LocalCatalog.RestoreAsync(backup, restored, 10_000_000);
        using var reopened = LocalCatalog.Open(restored);
        Assert.Equal(5, reopened.SchemaVersion);
        var job = Assert.Single(reopened.ListProcessingJobs());
        Assert.Equal("canceled", job.State);
        Assert.DoesNotContain(reopened.ListProcessingJobs(), j => j.State == "running");
        Assert.Null(reopened.TryClaimProcessing(TimeSpan.FromSeconds(1)));
        Assert.True(reopened.RetryProcessing(job.ItemId, job.Revision, job.ProcessorKey));
        Assert.NotNull(reopened.TryClaimProcessing(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task VerifiedSchemaTwoSnapshotRestoresIntoSchemaFour()
    {
        var backup = Path.Combine(workspace, "legacy-backup");
        var restored = Path.Combine(workspace, "recovered");
        Directory.CreateDirectory(backup);
        Private(backup);
        var database = Path.Combine(backup, "catalog.sqlite");
        var sourceId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var original = new byte[] { 4, 5, 6 };
        var digest = Convert.ToHexStringLower(SHA256.HashData(original));
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString()))
        {
            db.Open();
            foreach (var name in new[] { "LocalCatalog.sql", "LocalCatalog.V2.sql" })
            {
                using var resource = typeof(LocalCatalog).Assembly.GetManifestResourceStream(
                    "Praxis.Storage." + name)!;
                using var reader = new StreamReader(resource);
                using var command = db.CreateCommand();
                command.CommandText = reader.ReadToEnd();
                command.ExecuteNonQuery();
            }

            using var insert = db.CreateCommand();
            insert.CommandText = """
                INSERT INTO original_objects VALUES ($digest, 3, '2026-11-08T00:00:00Z');
                INSERT INTO sources (source_id, source_kind, locator, created_utc)
                    VALUES ($source, 'file', $locator, '2026-11-08T00:00:00Z');
                INSERT INTO items VALUES ($item, $source);
                INSERT INTO item_versions VALUES ($item, 1, $digest, '2026-11-08T00:00:00Z');
                INSERT INTO ingest_requests
                    (request_key, source_id, state, attempt, failure_code, revision)
                    VALUES ('old-receipt', $source, 'committed', 1, NULL, 1);
                """;
            insert.Parameters.AddWithValue("$digest", digest);
            insert.Parameters.AddWithValue("$source", sourceId.ToString("D"));
            insert.Parameters.AddWithValue("$item", itemId.ToString("D"));
            insert.Parameters.AddWithValue("$locator", Source("legacy"));
            insert.ExecuteNonQuery();
        }

        var directory = Path.Combine(backup, "objects", digest[..2], digest[2..4]);
        Directory.CreateDirectory(directory);
        Private(Path.Combine(backup, "objects"));
        Private(Path.Combine(backup, "objects", digest[..2]));
        Private(directory);
        File.WriteAllBytes(Path.Combine(directory, digest), original);
        var databaseBytes = await File.ReadAllBytesAsync(database);
        var json = JsonSerializer.Serialize(new
        {
            FormatVersion = 1,
            CatalogVersion = 2,
            CatalogSha256 = Convert.ToHexStringLower(SHA256.HashData(databaseBytes)),
            CatalogLength = databaseBytes.LongLength,
            Objects = new[] { new { Digest = digest, Length = original.LongLength } }
        });
        await File.WriteAllTextAsync(Path.Combine(backup, "manifest.json"), json);
        Assert.Equal(1, (await LocalCatalog.VerifyBackupAsync(backup, 10_000_000)).ObjectCount);
        await LocalCatalog.RestoreAsync(backup, restored, 10_000_000);
        using var catalog = LocalCatalog.Open(restored);
        Assert.Equal(5, catalog.SchemaVersion);
        Assert.Equal("queued", Assert.Single(catalog.ListProcessingJobs()).State);
        using var replay = new MemoryStream();
        var accepted = await catalog.CaptureFileAsync(Source("legacy"), replay, 0, "old-receipt");
        Assert.Equal(sourceId, accepted.SourceId);
        Assert.Equal(itemId, accepted.ItemId);
        Assert.Equal(1, accepted.Revision);
    }

    [Fact]
    public void PublicJobOperationsRejectUnboundedArguments()
    {
        using var catalog = LocalCatalog.Open(root);
        Assert.Throws<ArgumentOutOfRangeException>(() => catalog.ListProcessingJobs(501));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            catalog.TryClaimProcessing(TimeSpan.FromHours(2)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            catalog.TryClaimProcessing(TimeSpan.FromSeconds(1), 17));
        Assert.Throws<ArgumentException>(() =>
            catalog.QueueProcessing(Guid.NewGuid(), 1, "../not-a-processor"));
        Assert.False(catalog.RevokeSource(Guid.NewGuid()));
    }

    private string Source(string name) => Path.Combine(workspace, "submitted", name);

    private static void Private(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, true);
        }
    }
}
