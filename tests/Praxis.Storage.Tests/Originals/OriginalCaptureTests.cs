using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Praxis.Storage.Catalog;
using Praxis.Storage.Originals;
using Xunit;

namespace Praxis.Storage.Tests.Originals;

public sealed class OriginalCaptureTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "praxis-original-test-" + Guid.NewGuid().ToString("N"));

    public OriginalCaptureTests()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task MultiChunkOriginalSurvivesCatalogRestartWithExactBytes()
    {
        byte[] bytes = Enumerable.Range(0, 150_000).Select(i => (byte)(i * 37)).ToArray();
        CapturedOriginal captured;
        using (LocalCatalog catalog = LocalCatalog.Open(root))
        {
            using MemoryStream content = new(bytes);
            captured = await catalog.Originals.CaptureFileAsync(SourcePath("large.bin"), content, bytes.Length, Key());
            Assert.True(content.CanRead);
            await catalog.Originals.VerifyAsync(captured.ItemId);
        }

        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), captured.Sha256);
        Assert.Equal(bytes.Length, captured.Length);
        Assert.Equal(bytes, File.ReadAllBytes(ObjectPath(captured.Sha256)));
        using LocalCatalog reopened = LocalCatalog.Open(root);
        await reopened.Originals.VerifyAsync(captured.ItemId);
        using SqliteConnection reader = Connect(reopened.DatabasePath);
        Assert.Equal(1L, Scalar(reader, "SELECT COUNT(*) FROM sources"));
        Assert.Equal(1L, Scalar(reader, "SELECT COUNT(*) FROM items"));
        Assert.Equal(1L, Scalar(reader, "SELECT COUNT(*) FROM item_versions"));
        Assert.Equal(1L, Scalar(reader, "SELECT COUNT(*) FROM original_objects"));
    }

    [Fact]
    public async Task DistinctSourcesShareOriginalBytesWithoutMergingProvenance()
    {
        byte[] bytes = [0, 1, 255, 3, 5, 8];
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using MemoryStream first = new(bytes);
        using MemoryStream second = new(bytes);
        CapturedOriginal a = await catalog.Originals.CaptureFileAsync(SourcePath("first.bin"), first, 100, Key());
        CapturedOriginal b = await catalog.Originals.CaptureFileAsync(SourcePath("second.bin"), second, 100, Key());

        Assert.NotEqual(a.SourceId, b.SourceId);
        Assert.NotEqual(a.ItemId, b.ItemId);
        Assert.Equal(a.Sha256, b.Sha256);
        await catalog.Originals.VerifyAsync(a.ItemId);
        await catalog.Originals.VerifyAsync(b.ItemId);
        using SqliteConnection db = Connect(catalog.DatabasePath);
        Assert.Equal(2L, Scalar(db, "SELECT COUNT(*) FROM sources"));
        Assert.Equal(2L, Scalar(db, "SELECT COUNT(*) FROM item_versions"));
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM original_objects"));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(ObjectPath(a.Sha256))!));
    }

    [Fact]
    public async Task ConcurrentIdenticalCapturesPublishOneVerifiedObject()
    {
        byte[] bytes = Enumerable.Range(0, 100_000).Select(i => (byte)(i * 17)).ToArray();
        using LocalCatalog catalog = LocalCatalog.Open(root);
        CapturedOriginal[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            using MemoryStream stream = new(bytes);
            return await catalog.Originals.CaptureFileAsync(SourcePath($"concurrent-{i}.bin"),
                stream, bytes.Length, Key());
        }));

        Assert.Equal(8, results.Select(x => x.SourceId).Distinct().Count());
        Assert.Equal(8, results.Select(x => x.ItemId).Distinct().Count());
        Assert.Single(results.Select(x => x.Sha256).Distinct());
        foreach (CapturedOriginal result in results)
        {
            await catalog.Originals.VerifyAsync(result.ItemId);
        }

        using SqliteConnection db = Connect(catalog.DatabasePath);
        Assert.Equal(8L, Scalar(db, "SELECT COUNT(*) FROM sources"));
        Assert.Equal(8L, Scalar(db, "SELECT COUNT(*) FROM item_versions"));
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM original_objects"));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "staging")));
    }

    [Fact]
    public async Task EmptyOriginalIsPreserved()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using MemoryStream empty = new();
        CapturedOriginal result = await catalog.Originals.CaptureFileAsync(SourcePath("empty.md"), empty, 0, Key());

        Assert.Equal(0, result.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData([])), result.Sha256);
        Assert.Equal(0, new FileInfo(ObjectPath(result.Sha256)).Length);
        await catalog.Originals.VerifyAsync(result.ItemId);
    }

    [Fact]
    public async Task NonSeekableInputUsesBoundedReadChunks()
    {
        byte[] bytes = Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray();
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using NonSeekableStream input = new(bytes);
        CapturedOriginal result = await catalog.Originals.CaptureFileAsync(SourcePath("stream.bin"), input, bytes.Length, Key());

        Assert.Equal(bytes.Length, result.Length);
        Assert.True(input.LargestRead <= 64 * 1024);
        await catalog.Originals.VerifyAsync(result.ItemId);
        Assert.Equal(bytes, File.ReadAllBytes(ObjectPath(result.Sha256)));
    }

    [Fact]
    public async Task OversizedOriginalLeavesNoCatalogReferencesOrStagingFile()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using MemoryStream bytes = new(new byte[80_000]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.Originals.CaptureFileAsync(SourcePath("too-large.bin"), bytes, 1024, Key()));

        Assert.Empty(Directory.GetFiles(Path.Combine(root, "staging")));
        using SqliteConnection db = Connect(catalog.DatabasePath);
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM original_objects"));
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM sources"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "objects"), "*",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CancellationRemovesStagingWithoutCommittingMetadata()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using MemoryStream bytes = new(new byte[2000]);
        using CancellationTokenSource canceled = new();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            catalog.Originals.CaptureFileAsync(SourcePath("canceled.bin"), bytes, 100_000, Key(), canceled.Token));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "staging")));
        using SqliteConnection db = Connect(catalog.DatabasePath);
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM sources"));
    }

    [Fact]
    public async Task FailedSourceReadCannotCommitIncompleteContent()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using FailingReadStream failing = new([1, 2, 3, 4]);

        await Assert.ThrowsAsync<IOException>(() =>
            catalog.Originals.CaptureFileAsync(SourcePath("broken.bin"), failing, 100, Key()));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "staging")));
        using SqliteConnection db = Connect(catalog.DatabasePath);
        Assert.Equal(0L, Scalar(db, "SELECT COUNT(*) FROM sources"));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(root, "objects"), "*",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ReimportAppendsRevisionWithoutChangingSourceIdentity()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using MemoryStream original = new([1, 2, 3]);
        CapturedOriginal first = await catalog.Originals.CaptureFileAsync(SourcePath("same.bin"), original, 100, Key());
        using MemoryStream changed = new([4, 5, 6]);
        CapturedOriginal second = await catalog.Originals.CaptureFileAsync(SourcePath("same.bin"), changed, 100, Key());

        Assert.Equal(first.SourceId, second.SourceId);
        Assert.Equal(first.ItemId, second.ItemId);
        Assert.Equal(1, first.Revision);
        Assert.Equal(2, second.Revision);
        Assert.NotEqual(first.Sha256, second.Sha256);
        await catalog.Originals.VerifyAsync(first.ItemId);
        using SqliteConnection db = Connect(catalog.DatabasePath);
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM sources"));
        Assert.Equal(2L, Scalar(db, "SELECT COUNT(*) FROM item_versions"));
        Assert.Equal(2L, Scalar(db, "SELECT COUNT(*) FROM original_objects"));
        Assert.Equal(2L, Scalar(db, "SELECT COUNT(*) FROM ingest_requests"));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "staging")));
    }

    [Fact]
    public async Task CorruptExistingObjectIsRejectedRatherThanReplaced()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using MemoryStream input = new([4, 5, 6, 7]);
        CapturedOriginal result = await catalog.Originals.CaptureFileAsync(SourcePath("good.bin"), input, 100, Key());
        string path = ObjectPath(result.Sha256);
        File.WriteAllBytes(path, [9, 5, 6, 7]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.Originals.VerifyAsync(result.ItemId));

        using MemoryStream duplicate = new([4, 5, 6, 7]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            catalog.Originals.CaptureFileAsync(SourcePath("new-source.bin"), duplicate, 100, Key()));
        Assert.Equal(new byte[] { 9, 5, 6, 7 }, File.ReadAllBytes(path));
        using SqliteConnection db = Connect(catalog.DatabasePath);
        Assert.Equal(1L, Scalar(db, "SELECT COUNT(*) FROM sources"));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "staging")));
    }

    [Fact]
    public async Task SymlinkedObjectDirectoryIsRejectedWithoutWritingOutsideRoot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using LocalCatalog catalog = LocalCatalog.Open(root);
        string external = Path.Combine(Path.GetTempPath(), "praxis-untrusted-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "objects"), external);
            using MemoryStream input = new([1, 2, 3]);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                catalog.Originals.CaptureFileAsync(SourcePath("linked.bin"), input, 100, Key()));
            Assert.Empty(Directory.EnumerateFileSystemEntries(external));
        }
        finally
        {
            Directory.Delete(Path.Combine(root, "objects"));
            Directory.Delete(external);
        }
    }

    [Fact]
    public async Task MissingOriginalOrUnknownItemFailsVerification()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        using MemoryStream input = new([1, 2]);
        CapturedOriginal result = await catalog.Originals.CaptureFileAsync(SourcePath("missing.bin"), input, 100, Key());
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            catalog.Originals.VerifyAsync(Guid.NewGuid()));
        File.Delete(ObjectPath(result.Sha256));
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            catalog.Originals.VerifyAsync(result.ItemId));
    }

    private static string Key() => Guid.NewGuid().ToString("N");

    private string SourcePath(string name) => Path.Combine(root, "unopened-source", name);

    private string ObjectPath(string digest) => Path.Combine(root, "objects",
        digest[..2], digest[2..4], digest);

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

    private static long Scalar(SqliteConnection db, string sql)
    {
        using SqliteCommand command = db.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        public int LargestRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            LargestRead = Math.Max(LargestRead, buffer.Length);
            return inner.ReadAsync(buffer, token);
        }
        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, count);
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FailingReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        private bool read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (read)
            {
                throw new IOException("Simulated source failure.");
            }

            read = true;
            return base.ReadAsync(buffer, token);
        }
    }
}
