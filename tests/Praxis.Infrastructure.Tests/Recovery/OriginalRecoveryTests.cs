using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Praxis.Infrastructure.Catalog;
using Praxis.Infrastructure.Originals;
using Praxis.Infrastructure.Recovery;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Recovery;

public sealed class OriginalRecoveryTests : IDisposable
{
    private readonly string root = Path.Combine(TestPaths.Temp,
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
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using MemoryStream first = new([1]);
        CapturedOriginal a = await catalog.Originals.CaptureFileAsync(Source("one.md"), first, 10, "one-a");
        using MemoryStream changed = new([2]);
        await catalog.Originals.CaptureFileAsync(Source("one.md"), changed, 10, "one-b");
        using MemoryStream shared = new([1]);
        await catalog.Originals.CaptureFileAsync(Source("two.md"), shared, 10, "two-a");

        OriginalRecoveryInspection report = await catalog.Recovery.InspectAsync();
        Assert.Equal(new OriginalRecoveryInspection(0, 2, 2, 0, 0, 0), report);
        Assert.True(File.Exists(ObjectPath(a.Sha256)));
    }

    [Fact]
    public async Task TransactionFailureLeavesInspectableUnreferencedObjectWithoutDeletingIt()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using MemoryStream first = new([1]);
        await catalog.Originals.CaptureFileAsync(Source("revoked.md"), first, 10, "accepted");
        using (SqliteConnection db = Connect(catalog.DatabasePath))
        {
            using SqliteCommand command = db.CreateCommand();
            command.CommandText = "UPDATE sources SET status='revoked'";
            command.ExecuteNonQuery();
        }

        byte[] unpublished = [6, 7, 8];
        using MemoryStream second = new(unpublished);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.Originals.CaptureFileAsync(Source("revoked.md"), second, 10, "rejected"));

        string digest = Convert.ToHexStringLower(SHA256.HashData(unpublished));
        string path = ObjectPath(digest);
        Assert.Equal(unpublished, File.ReadAllBytes(path));
        OriginalRecoveryInspection report = await catalog.Recovery.InspectAsync();
        Assert.Equal(new OriginalRecoveryInspection(0, 2, 1, 1, 0, 0), report);
        Assert.True(File.Exists(path));
        using SqliteConnection db2 = Connect(catalog.DatabasePath);
        Assert.Equal(1L, Count(db2, "item_versions"));
        Assert.Equal(1L, Count(db2, "ingest_requests"));
    }

    [Fact]
    public async Task StagingInventoryIsReadOnlyAndSurvivesReopen()
    {
        using (LocalCatalog catalog = LocalCatalog.Open(root))
        {
            using MemoryStream input = new([1]);
            await catalog.Originals.CaptureFileAsync(Source("note.md"), input, 10, "one");
        }

        string pending = Path.Combine(root, "staging", $"{Guid.NewGuid():N}.part");
        File.WriteAllBytes(pending, [9, 9, 9]);

        using LocalCatalog reopened = LocalCatalog.Open(root);
        OriginalRecoveryInspection report = await reopened.Recovery.InspectAsync();
        Assert.Equal(1, report.StagedFiles);
        Assert.Equal(1, report.PublishedObjects);
        Assert.Equal(0, report.MissingObjects);
        Assert.Equal(new byte[] { 9, 9, 9 }, File.ReadAllBytes(pending));
        Assert.Equal(report, await reopened.Recovery.InspectAsync());
    }

    [Fact]
    public async Task MissingAndCorruptReferencedContentIsReportedWithoutRepairingIt()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using MemoryStream lost = new([1, 2]);
        CapturedOriginal first = await catalog.Originals.CaptureFileAsync(Source("lost.md"), lost, 10, "a");
        using MemoryStream altered = new([3, 4]);
        CapturedOriginal second = await catalog.Originals.CaptureFileAsync(Source("corrupt.md"), altered, 10, "b");

        File.Delete(ObjectPath(first.Sha256));
        File.WriteAllBytes(ObjectPath(second.Sha256), [5, 4]);
        OriginalRecoveryInspection report = await catalog.Recovery.InspectAsync();
        Assert.Equal(new OriginalRecoveryInspection(0, 1, 2, 0, 1, 1), report);
        Assert.Equal(new byte[] { 5, 4 }, File.ReadAllBytes(ObjectPath(second.Sha256)));
    }

    [Fact]
    public async Task UnexpectedStorageFileFailsInventoryWithoutCleaningIt()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using MemoryStream source = new([1]);
        CapturedOriginal result = await catalog.Originals.CaptureFileAsync(Source("a.md"), source, 10, "key");
        string invalid = Path.Combine(root, "objects", result.Sha256[..2], result.Sha256[2..4], "unexpected.txt");
        File.WriteAllText(invalid, "must remain");
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.Recovery.InspectAsync());
        Assert.Equal("must remain", File.ReadAllText(invalid));
    }

    [Fact]
    public async Task CanceledInspectionDoesNotMutateOriginals()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using MemoryStream input = new([1, 2, 3]);
        CapturedOriginal captured = await catalog.Originals.CaptureFileAsync(Source("a.md"), input, 10, "one");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            catalog.Recovery.InspectAsync(cancellation.Token));
        Assert.True(File.Exists(ObjectPath(captured.Sha256)));
    }

    private string Source(string fileName) => Path.Combine(root, "submitted", fileName);

    private string ObjectPath(string digest) => Path.Combine(
        root, "objects", digest[..2], digest[2..4], digest);

    private static SqliteConnection Connect(string path)
    {
        SqliteConnection db = new(new SqliteConnectionStringBuilder
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
        using SqliteCommand command = db.CreateCommand();
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
