using Microsoft.Data.Sqlite;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class OriginalReceiptTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "praxis-receipt-test-" + Guid.NewGuid().ToString("N"));

    public OriginalReceiptTests()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task CommittedKeyRetriesWithoutReadingStreamAfterRestart()
    {
        const string key = "initial-request";
        CapturedOriginal first;
        using (LocalCatalog catalog = LocalCatalog.Open(root))
        {
            using MemoryStream initial = new MemoryStream([1, 2, 3]);
            first = await catalog.CaptureFileAsync(Source("note.md"), initial, 10, key);
            Assert.Equal(1, first.Revision);
        }

        using LocalCatalog reopened = LocalCatalog.Open(root);
        using ThrowOnReadStream neverRead = new ThrowOnReadStream();
        CapturedOriginal retry = await reopened.CaptureFileAsync(Source("note.md"), neverRead, 10, key);
        Assert.Equal(first, retry);
        Assert.False(neverRead.WasRead);
        using SqliteConnection db = Connect(reopened.DatabasePath);
        Assert.Equal(1L, Count(db, "sources"));
        Assert.Equal(1L, Count(db, "item_versions"));
        Assert.Equal(1L, Count(db, "ingest_requests"));
    }

    [Fact]
    public async Task NewKeyForChangedContentAddsRevisionWithoutLosingOriginal()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using MemoryStream initial = new MemoryStream([10, 20]);
        CapturedOriginal first = await catalog.CaptureFileAsync(Source("versioned.md"), initial, 10, "first");
        using MemoryStream changed = new MemoryStream([30, 40]);
        CapturedOriginal next = await catalog.CaptureFileAsync(Source("versioned.md"), changed, 10, "second");

        Assert.Equal(first.SourceId, next.SourceId);
        Assert.Equal(first.ItemId, next.ItemId);
        Assert.Equal(1, first.Revision);
        Assert.Equal(2, next.Revision);
        Assert.NotEqual(first.Sha256, next.Sha256);

        using ThrowOnReadStream retryInput = new ThrowOnReadStream();
        CapturedOriginal replay = await catalog.CaptureFileAsync(Source("versioned.md"), retryInput, 10, "first");
        Assert.Equal(first, replay);
        Assert.False(retryInput.WasRead);
        await catalog.VerifyOriginalAsync(first.ItemId);
        using SqliteConnection db = Connect(catalog.DatabasePath);
        Assert.Equal(2L, Count(db, "item_versions"));
        Assert.Equal(2L, Count(db, "ingest_requests"));
        Assert.Equal(2L, Count(db, "original_objects"));
    }

    [Fact]
    public async Task UnchangedContentWithNewKeyRecordsReceiptWithoutNewRevision()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using MemoryStream firstInput = new MemoryStream([1, 2, 3]);
        CapturedOriginal first = await catalog.CaptureFileAsync(Source("same.md"), firstInput, 10, "a");
        using MemoryStream secondInput = new MemoryStream([1, 2, 3]);
        CapturedOriginal second = await catalog.CaptureFileAsync(Source("same.md"), secondInput, 10, "b");
        Assert.Equal(first, second);

        using SqliteConnection db = Connect(catalog.DatabasePath);
        Assert.Equal(1L, Count(db, "item_versions"));
        Assert.Equal(2L, Count(db, "ingest_requests"));
    }

    [Fact]
    public async Task ReintroducingHistoricalBytesRecordsNewRevisionButReusesObject()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using MemoryStream a = new MemoryStream([1]);
        CapturedOriginal first = await catalog.CaptureFileAsync(Source("history.md"), a, 10, "a");
        using MemoryStream b = new MemoryStream([2]);
        await catalog.CaptureFileAsync(Source("history.md"), b, 10, "b");
        using MemoryStream again = new MemoryStream([1]);
        CapturedOriginal third = await catalog.CaptureFileAsync(Source("history.md"), again, 10, "c");

        Assert.Equal(first.ItemId, third.ItemId);
        Assert.Equal(first.Sha256, third.Sha256);
        Assert.Equal(3, third.Revision);
        using SqliteConnection db = Connect(catalog.DatabasePath);
        Assert.Equal(3L, Count(db, "item_versions"));
        Assert.Equal(2L, Count(db, "original_objects"));
        Assert.Equal(3L, Count(db, "ingest_requests"));
    }

    [Fact]
    public async Task FailedRequestKeyCanBeRetriedWithoutAStaleReceipt()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using MemoryStream oversized = new MemoryStream(new byte[8]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.CaptureFileAsync(Source("retry.md"), oversized, 4, "attempt"));
        using MemoryStream retry = new MemoryStream([1, 2, 3]);
        CapturedOriginal captured = await catalog.CaptureFileAsync(Source("retry.md"), retry, 4, "attempt");
        Assert.Equal(1, captured.Revision);
        using SqliteConnection db = Connect(catalog.DatabasePath);
        Assert.Equal(1L, Count(db, "ingest_requests"));
    }

    [Fact]
    public async Task RequestKeysCannotCrossSourceBoundaries()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using MemoryStream input = new MemoryStream([1]);
        await catalog.CaptureFileAsync(Source("one.md"), input, 10, "bound-key");
        using ThrowOnReadStream other = new ThrowOnReadStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.CaptureFileAsync(Source("two.md"), other, 10, "bound-key"));
        Assert.False(other.WasRead);
    }

    [Fact]
    public async Task ParallelCaptureOfSameRequestCreatesOneCommittedRevision()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        CapturedOriginal[] results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using MemoryStream input = new MemoryStream(new byte[100_000]);
            return await catalog.CaptureFileAsync(Source("parallel.bin"), input, 100_000, "shared-key");
        }));
        Assert.All(results, x => Assert.Equal(results[0], x));
        using SqliteConnection db = Connect(catalog.DatabasePath);
        Assert.Equal(1L, Count(db, "sources"));
        Assert.Equal(1L, Count(db, "item_versions"));
        Assert.Equal(1L, Count(db, "ingest_requests"));
    }

    [Fact]
    public async Task ParallelKeyConflictRejectsDifferentContentWithoutCommittingAnotherVersion()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        PairGate gate = new PairGate();
        async Task<CapturedOriginal> Capture(byte value)
        {
            using GatedReadStream input = new GatedReadStream(Enumerable.Repeat(value, 80_000).ToArray(), gate);
            return await catalog.CaptureFileAsync(Source("parallel.md"), input, 80_000, "key");
        }

        Task<CapturedOriginal>[] tasks = new[] { Capture(1), Capture(2) };
        try
        {
            await Task.WhenAll(tasks);
            Assert.Fail("A request key cannot commit two different contents.");
        }
        catch (InvalidOperationException)
        {
            Assert.Equal(1, tasks.Count(x => x.Status == TaskStatus.RanToCompletion));
        }

        using SqliteConnection db = Connect(catalog.DatabasePath);
        Assert.Equal(1L, Count(db, "sources"));
        Assert.Equal(1L, Count(db, "item_versions"));
        Assert.Equal(1L, Count(db, "ingest_requests"));
    }

    [Fact]
    public async Task RevokedSourcesRejectRecaptureAndReceiptReplays()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using MemoryStream input = new MemoryStream([3]);
        await catalog.CaptureFileAsync(Source("removed.md"), input, 10, "first");
        using (SqliteConnection db = Connect(catalog.DatabasePath))
        {
            using SqliteCommand command = db.CreateCommand();
            command.CommandText = "UPDATE sources SET status='revoked'";
            command.ExecuteNonQuery();
        }

        using MemoryStream fresh = new MemoryStream([4]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.CaptureFileAsync(Source("removed.md"), fresh, 10, "next"));
        using ThrowOnReadStream replay = new ThrowOnReadStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.CaptureFileAsync(Source("removed.md"), replay, 10, "first"));
        Assert.False(replay.WasRead);
    }

    [Fact]
    public async Task FailedAndCanceledReadsNeverCommitReceipts()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using CancellationTokenSource canceled = new CancellationTokenSource();
        canceled.Cancel();
        using MemoryStream input = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            catalog.CaptureFileAsync(Source("canceled.md"), input, 10, "canceled", canceled.Token));
        using MemoryStream oversized = new MemoryStream(new byte[5]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.CaptureFileAsync(Source("oversized.md"), oversized, 1, "oversized"));
        using SqliteConnection db = Connect(catalog.DatabasePath);
        Assert.Equal(0L, Count(db, "sources"));
        Assert.Equal(0L, Count(db, "ingest_requests"));
    }

    [Fact]
    public async Task TamperedHistoricalVersionBlocksItsReceiptReplay()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using MemoryStream firstInput = new MemoryStream([1]);
        CapturedOriginal first = await catalog.CaptureFileAsync(Source("history.md"), firstInput, 10, "first");
        using MemoryStream secondInput = new MemoryStream([2]);
        await catalog.CaptureFileAsync(Source("history.md"), secondInput, 10, "second");

        File.WriteAllBytes(Path.Combine(root, "objects", first.Sha256[..2],
            first.Sha256[2..4], first.Sha256), [9]);
        using ThrowOnReadStream replay = new ThrowOnReadStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.CaptureFileAsync(Source("history.md"), replay, 10, "first"));
    }

    private string Source(string file) => Path.Combine(root, "submitted", file);

    private static SqliteConnection Connect(string databasePath)
    {
        SqliteConnection db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
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

    private sealed class PairGate
    {
        private int entered;
        private readonly TaskCompletionSource<bool> ready =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task WaitAsync(CancellationToken token)
        {
            if (Interlocked.Increment(ref entered) == 2)
            {
                ready.TrySetResult(true);
            }

            await ready.Task.WaitAsync(token);
        }
    }

    private sealed class GatedReadStream(byte[] bytes, PairGate gate) : MemoryStream(bytes)
    {
        private bool entered;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken token = default)
        {
            if (!entered)
            {
                entered = true;
                await gate.WaitAsync(token);
            }

            return await base.ReadAsync(buffer, token);
        }
    }

    private sealed class ThrowOnReadStream : MemoryStream
    {
        public bool WasRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            WasRead = true;
            throw new IOException("The completed request must not reread its stream.");
        }
    }
}
