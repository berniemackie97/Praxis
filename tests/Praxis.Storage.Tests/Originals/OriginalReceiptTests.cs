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
        using (var catalog = LocalCatalog.Open(root))
        {
            using var initial = new MemoryStream([1, 2, 3]);
            first = await catalog.CaptureFileAsync(Source("note.md"), initial, 10, key);
            Assert.Equal(1, first.Revision);
        }

        using var reopened = LocalCatalog.Open(root);
        using var neverRead = new ThrowOnReadStream();
        var retry = await reopened.CaptureFileAsync(Source("note.md"), neverRead, 10, key);
        Assert.Equal(first, retry);
        Assert.False(neverRead.WasRead);
        using var db = Connect(reopened.DatabasePath);
        Assert.Equal(1L, Count(db, "sources"));
        Assert.Equal(1L, Count(db, "item_versions"));
        Assert.Equal(1L, Count(db, "ingest_requests"));
    }

    [Fact]
    public async Task NewKeyForChangedContentAddsRevisionWithoutLosingOriginal()
    {
        using var catalog = LocalCatalog.Open(root);
        using var initial = new MemoryStream([10, 20]);
        var first = await catalog.CaptureFileAsync(Source("versioned.md"), initial, 10, "first");
        using var changed = new MemoryStream([30, 40]);
        var next = await catalog.CaptureFileAsync(Source("versioned.md"), changed, 10, "second");

        Assert.Equal(first.SourceId, next.SourceId);
        Assert.Equal(first.ItemId, next.ItemId);
        Assert.Equal(1, first.Revision);
        Assert.Equal(2, next.Revision);
        Assert.NotEqual(first.Sha256, next.Sha256);

        using var retryInput = new ThrowOnReadStream();
        var replay = await catalog.CaptureFileAsync(Source("versioned.md"), retryInput, 10, "first");
        Assert.Equal(first, replay);
        Assert.False(retryInput.WasRead);
        await catalog.VerifyOriginalAsync(first.ItemId);
        using var db = Connect(catalog.DatabasePath);
        Assert.Equal(2L, Count(db, "item_versions"));
        Assert.Equal(2L, Count(db, "ingest_requests"));
        Assert.Equal(2L, Count(db, "original_objects"));
    }

    [Fact]
    public async Task UnchangedContentWithNewKeyRecordsReceiptWithoutNewRevision()
    {
        using var catalog = LocalCatalog.Open(root);
        using var firstInput = new MemoryStream([1, 2, 3]);
        var first = await catalog.CaptureFileAsync(Source("same.md"), firstInput, 10, "a");
        using var secondInput = new MemoryStream([1, 2, 3]);
        var second = await catalog.CaptureFileAsync(Source("same.md"), secondInput, 10, "b");
        Assert.Equal(first, second);

        using var db = Connect(catalog.DatabasePath);
        Assert.Equal(1L, Count(db, "item_versions"));
        Assert.Equal(2L, Count(db, "ingest_requests"));
    }

    [Fact]
    public async Task ReintroducingHistoricalBytesRecordsNewRevisionButReusesObject()
    {
        using var catalog = LocalCatalog.Open(root);
        using var a = new MemoryStream([1]);
        var first = await catalog.CaptureFileAsync(Source("history.md"), a, 10, "a");
        using var b = new MemoryStream([2]);
        await catalog.CaptureFileAsync(Source("history.md"), b, 10, "b");
        using var again = new MemoryStream([1]);
        var third = await catalog.CaptureFileAsync(Source("history.md"), again, 10, "c");

        Assert.Equal(first.ItemId, third.ItemId);
        Assert.Equal(first.Sha256, third.Sha256);
        Assert.Equal(3, third.Revision);
        using var db = Connect(catalog.DatabasePath);
        Assert.Equal(3L, Count(db, "item_versions"));
        Assert.Equal(2L, Count(db, "original_objects"));
        Assert.Equal(3L, Count(db, "ingest_requests"));
    }

    [Fact]
    public async Task FailedRequestKeyCanBeRetriedWithoutAStaleReceipt()
    {
        using var catalog = LocalCatalog.Open(root);
        using var oversized = new MemoryStream(new byte[8]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.CaptureFileAsync(Source("retry.md"), oversized, 4, "attempt"));
        using var retry = new MemoryStream([1, 2, 3]);
        var captured = await catalog.CaptureFileAsync(Source("retry.md"), retry, 4, "attempt");
        Assert.Equal(1, captured.Revision);
        using var db = Connect(catalog.DatabasePath);
        Assert.Equal(1L, Count(db, "ingest_requests"));
    }

    [Fact]
    public async Task RequestKeysCannotCrossSourceBoundaries()
    {
        using var catalog = LocalCatalog.Open(root);
        using var input = new MemoryStream([1]);
        await catalog.CaptureFileAsync(Source("one.md"), input, 10, "bound-key");
        using var other = new ThrowOnReadStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.CaptureFileAsync(Source("two.md"), other, 10, "bound-key"));
        Assert.False(other.WasRead);
    }

    [Fact]
    public async Task ParallelCaptureOfSameRequestCreatesOneCommittedRevision()
    {
        using var catalog = LocalCatalog.Open(root);
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            using var input = new MemoryStream(new byte[100_000]);
            return await catalog.CaptureFileAsync(Source("parallel.bin"), input, 100_000, "shared-key");
        }));
        Assert.All(results, x => Assert.Equal(results[0], x));
        using var db = Connect(catalog.DatabasePath);
        Assert.Equal(1L, Count(db, "sources"));
        Assert.Equal(1L, Count(db, "item_versions"));
        Assert.Equal(1L, Count(db, "ingest_requests"));
    }

    [Fact]
    public async Task ParallelKeyConflictRejectsDifferentContentWithoutCommittingAnotherVersion()
    {
        using var catalog = LocalCatalog.Open(root);
        var gate = new PairGate();
        async Task<CapturedOriginal> Capture(byte value)
        {
            using var input = new GatedReadStream(Enumerable.Repeat(value, 80_000).ToArray(), gate);
            return await catalog.CaptureFileAsync(Source("parallel.md"), input, 80_000, "key");
        }

        var tasks = new[] { Capture(1), Capture(2) };
        try
        {
            await Task.WhenAll(tasks);
            Assert.Fail("A request key cannot commit two different contents.");
        }
        catch (InvalidOperationException)
        {
            Assert.Equal(1, tasks.Count(x => x.Status == TaskStatus.RanToCompletion));
        }

        using var db = Connect(catalog.DatabasePath);
        Assert.Equal(1L, Count(db, "sources"));
        Assert.Equal(1L, Count(db, "item_versions"));
        Assert.Equal(1L, Count(db, "ingest_requests"));
    }

    [Fact]
    public async Task RevokedSourcesRejectRecaptureAndReceiptReplays()
    {
        using var catalog = LocalCatalog.Open(root);
        using var input = new MemoryStream([3]);
        await catalog.CaptureFileAsync(Source("removed.md"), input, 10, "first");
        using (var db = Connect(catalog.DatabasePath))
        {
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE sources SET status='revoked'";
            command.ExecuteNonQuery();
        }

        using var fresh = new MemoryStream([4]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.CaptureFileAsync(Source("removed.md"), fresh, 10, "next"));
        using var replay = new ThrowOnReadStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.CaptureFileAsync(Source("removed.md"), replay, 10, "first"));
        Assert.False(replay.WasRead);
    }

    [Fact]
    public async Task FailedAndCanceledReadsNeverCommitReceipts()
    {
        using var catalog = LocalCatalog.Open(root);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        using var input = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            catalog.CaptureFileAsync(Source("canceled.md"), input, 10, "canceled", canceled.Token));
        using var oversized = new MemoryStream(new byte[5]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.CaptureFileAsync(Source("oversized.md"), oversized, 1, "oversized"));
        using var db = Connect(catalog.DatabasePath);
        Assert.Equal(0L, Count(db, "sources"));
        Assert.Equal(0L, Count(db, "ingest_requests"));
    }

    [Fact]
    public async Task TamperedHistoricalVersionBlocksItsReceiptReplay()
    {
        using var catalog = LocalCatalog.Open(root);
        using var firstInput = new MemoryStream([1]);
        var first = await catalog.CaptureFileAsync(Source("history.md"), firstInput, 10, "first");
        using var secondInput = new MemoryStream([2]);
        await catalog.CaptureFileAsync(Source("history.md"), secondInput, 10, "second");

        File.WriteAllBytes(Path.Combine(root, "objects", first.Sha256[..2],
            first.Sha256[2..4], first.Sha256), [9]);
        using var replay = new ThrowOnReadStream();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.CaptureFileAsync(Source("history.md"), replay, 10, "first"));
    }

    private string Source(string file) => Path.Combine(root, "submitted", file);

    private static SqliteConnection Connect(string databasePath)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
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
