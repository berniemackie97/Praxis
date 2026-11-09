using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class OriginalRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "praxis-recovery-test-" + Guid.NewGuid().ToString("N"));

    public OriginalRecoveryTests()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task CommittedHistoricalVersionsAndDeduplicatedObjectsAreReferenced()
    {
        using var catalog = LocalCatalog.Open(root);
        using var first = new MemoryStream([1]);
        var a = await catalog.CaptureFileAsync(Source("one.md"), first, 10, "one-a");
        using var changed = new MemoryStream([2]);
        await catalog.CaptureFileAsync(Source("one.md"), changed, 10, "one-b");
        using var shared = new MemoryStream([1]);
        await catalog.CaptureFileAsync(Source("two.md"), shared, 10, "two-a");

        var report = await catalog.InspectRecoveryAsync();
        Assert.Equal(new OriginalRecoveryInspection(0, 2, 2, 0, 0, 0), report);
        Assert.True(File.Exists(ObjectPath(a.Sha256)));
    }

    [Fact]
    public async Task TransactionFailureLeavesInspectableUnreferencedObjectWithoutDeletingIt()
    {
        using var catalog = LocalCatalog.Open(root);
        using var first = new MemoryStream([1]);
        await catalog.CaptureFileAsync(Source("revoked.md"), first, 10, "accepted");
        using (var db = Connect(catalog.DatabasePath))
        {
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE sources SET status='revoked'";
            command.ExecuteNonQuery();
        }

        var unpublished = new byte[] { 6, 7, 8 };
        using var second = new MemoryStream(unpublished);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.CaptureFileAsync(Source("revoked.md"), second, 10, "rejected"));

        var digest = Convert.ToHexStringLower(SHA256.HashData(unpublished));
        var path = ObjectPath(digest);
        Assert.Equal(unpublished, File.ReadAllBytes(path));
        var report = await catalog.InspectRecoveryAsync();
        Assert.Equal(new OriginalRecoveryInspection(0, 2, 1, 1, 0, 0), report);
        Assert.True(File.Exists(path));
        using var db2 = Connect(catalog.DatabasePath);
        Assert.Equal(1L, Count(db2, "item_versions"));
        Assert.Equal(1L, Count(db2, "ingest_requests"));
    }

    [Fact]
    public async Task StagingInventoryIsReadOnlyAndSurvivesReopen()
    {
        using (var catalog = LocalCatalog.Open(root))
        {
            using var input = new MemoryStream([1]);
            await catalog.CaptureFileAsync(Source("note.md"), input, 10, "one");
        }

        var pending = Path.Combine(root, "staging", $"{Guid.NewGuid():N}.part");
        File.WriteAllBytes(pending, [9, 9, 9]);

        using var reopened = LocalCatalog.Open(root);
        var report = await reopened.InspectRecoveryAsync();
        Assert.Equal(1, report.StagedFiles);
        Assert.Equal(1, report.PublishedObjects);
        Assert.Equal(0, report.MissingObjects);
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(pending));
        Assert.Equal(report, await reopened.InspectRecoveryAsync());
    }

    [Fact]
    public async Task MissingAndCorruptReferencedContentIsReportedWithoutRepairingIt()
    {
        using var catalog = LocalCatalog.Open(root);
        using var lost = new MemoryStream([1, 2]);
        var first = await catalog.CaptureFileAsync(Source("lost.md"), lost, 10, "a");
        using var altered = new MemoryStream([3, 4]);
        var second = await catalog.CaptureFileAsync(Source("corrupt.md"), altered, 10, "b");

        File.Delete(ObjectPath(first.Sha256));
        File.WriteAllBytes(ObjectPath(second.Sha256), [5, 4]);
        var report = await catalog.InspectRecoveryAsync();
        Assert.Equal(new OriginalRecoveryInspection(0, 1, 2, 0, 1, 1), report);
        Assert.Equal(new byte[] { 5, 4 }, File.ReadAllBytes(ObjectPath(second.Sha256)));
    }

    [Fact]
    public async Task UnexpectedStorageFileFailsInventoryWithoutCleaningIt()
    {
        using var catalog = LocalCatalog.Open(root);
        using var source = new MemoryStream([1]);
        var result = await catalog.CaptureFileAsync(Source("a.md"), source, 10, "key");
        var invalid = Path.Combine(root, "objects", result.Sha256[..2], result.Sha256[2..4], "unexpected.txt");
        File.WriteAllText(invalid, "must remain");
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.InspectRecoveryAsync());
        Assert.Equal("must remain", File.ReadAllText(invalid));
    }

    [Fact]
    public async Task CanceledInspectionDoesNotMutateOriginals()
    {
        using var catalog = LocalCatalog.Open(root);
        using var input = new MemoryStream([1, 2, 3]);
        var captured = await catalog.CaptureFileAsync(Source("a.md"), input, 10, "one");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            catalog.InspectRecoveryAsync(cancellation.Token));
        Assert.True(File.Exists(ObjectPath(captured.Sha256)));
    }

    private string Source(string fileName) => Path.Combine(root, "submitted", fileName);

    private string ObjectPath(string digest) => Path.Combine(
        root, "objects", digest[..2], digest[2..4], digest);

    private static SqliteConnection Connect(string path)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        db.Open();
        return db;
    }

    private static long Count(SqliteConnection db, string table)
    {
        using var command = db.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
