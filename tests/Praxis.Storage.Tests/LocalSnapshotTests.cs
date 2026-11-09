using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class LocalSnapshotTests : IDisposable
{
    private readonly string workspace = Path.Combine(Path.GetTempPath(),
        "praxis-snapshot-test-" + Guid.NewGuid().ToString("N"));

    public LocalSnapshotTests()
    {
        Directory.CreateDirectory(workspace);
        Private(workspace);
        Directory.CreateDirectory(Live);
        Private(Live);
    }

    private string Live => Path.Combine(workspace, "live");
    private string Backup => Path.Combine(workspace, "snapshot");
    private string Restored => Path.Combine(workspace, "restored");
    private string Source(string name) => Path.Combine(workspace, "submitted", name);

    [Fact]
    public async Task OpenWalCatalogRestoresExactHistoryAndHistoricalReceipts()
    {
        CapturedOriginal old, latest, shared;
        using (LocalCatalog live = LocalCatalog.Open(Live))
        {
            using MemoryStream first = new MemoryStream([1, 2, 3]);
            old = await live.CaptureFileAsync(Source("note.md"), first, 100, "old");
            using MemoryStream changed = new MemoryStream([4, 5, 6]);
            latest = await live.CaptureFileAsync(Source("note.md"), changed, 100, "changed");
            using MemoryStream same = new MemoryStream([1, 2, 3]);
            shared = await live.CaptureFileAsync(Source("other.md"), same, 100, "shared");

            LocalSnapshotReport exported = await live.BackupAsync(Backup, 10_000_000);
            Assert.Equal(2, exported.ObjectCount);
            Assert.Equal(2, (await LocalCatalog.VerifyBackupAsync(Backup, 10_000_000)).ObjectCount);

            using MemoryStream later = new MemoryStream([7, 8]);
            await live.CaptureFileAsync(Source("note.md"), later, 100, "after-backup");
        }

        LocalSnapshotReport recovered = await LocalCatalog.RestoreAsync(Backup, Restored, 10_000_000);
        Assert.Equal(2, recovered.ObjectCount);
        using (LocalCatalog restored = LocalCatalog.Open(Restored))
        {
            using ThrowingReadStream notRead = new ThrowingReadStream();
            CapturedOriginal historical = await restored.CaptureFileAsync(
                Source("note.md"), notRead, 0, "old");
            Assert.Equal(old, historical);
            CapturedOriginal current = await restored.CaptureFileAsync(
                Source("note.md"), notRead, 0, "changed");
            Assert.Equal(latest, current);
            Assert.Equal(2, current.Revision);
            Assert.Equal(old.SourceId, current.SourceId);
            Assert.Equal(old.ItemId, current.ItemId);
            Assert.NotEqual(old.SourceId, shared.SourceId);
            await restored.VerifyOriginalAsync(old.ItemId);
            await restored.VerifyOriginalAsync(shared.ItemId);
            Assert.Equal(new OriginalRecoveryInspection(0, 2, 2, 0, 0, 0),
                await restored.InspectRecoveryAsync());
            using SqliteConnection db = Connect(restored.DatabasePath);
            Assert.Equal(3L, Count(db, "item_versions"));
            Assert.Equal(3L, Count(db, "ingest_requests"));
            Assert.Equal(2L, Count(db, "sources"));
        }

        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(ObjectPath(Restored, old.Sha256)));
        Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(ObjectPath(Restored, latest.Sha256)));
        Assert.False(File.Exists(ObjectPath(Restored,
            Convert.ToHexStringLower(SHA256.HashData([7, 8])))));
    }

    [Fact]
    public async Task SnapshotExcludesUncommittedPublishedObjects()
    {
        using LocalCatalog live = LocalCatalog.Open(Live);
        using MemoryStream first = new MemoryStream([1]);
        await live.CaptureFileAsync(Source("revoked.md"), first, 10, "first");
        using (SqliteConnection db = Connect(live.DatabasePath))
        {
            using SqliteCommand statement = db.CreateCommand();
            statement.CommandText = "UPDATE sources SET status='revoked'";
            statement.ExecuteNonQuery();
        }

        using MemoryStream rejected = new MemoryStream([9]);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            live.CaptureFileAsync(Source("revoked.md"), rejected, 10, "rejected"));
        string abandoned = Convert.ToHexStringLower(SHA256.HashData([9]));
        Assert.True(File.Exists(ObjectPath(Live, abandoned)));

        LocalSnapshotReport result = await live.BackupAsync(Backup, 10_000_000);
        Assert.Equal(1, result.ObjectCount);
        Assert.False(File.Exists(ObjectPath(Backup, abandoned)));
    }

    [Fact]
    public async Task MissingOriginalCannotProduceCompletedBackup()
    {
        using LocalCatalog live = LocalCatalog.Open(Live);
        using MemoryStream data = new MemoryStream([1, 2]);
        CapturedOriginal saved = await live.CaptureFileAsync(Source("a"), data, 10, "key");
        File.Delete(ObjectPath(Live, saved.Sha256));

        await Assert.ThrowsAnyAsync<IOException>(() =>
            live.BackupAsync(Backup, 10_000_000));
        Assert.False(Directory.Exists(Backup));
        Assert.Single(Directory.GetDirectories(workspace, ".snapshot.incomplete-*"));
    }

    [Fact]
    public async Task CorruptObjectAndTruncatedDatabasePreventRestore()
    {
        using (LocalCatalog live = LocalCatalog.Open(Live))
        {
            using MemoryStream content = new MemoryStream([1, 2, 3]);
            await live.CaptureFileAsync(Source("a"), content, 10, "one");
            await live.BackupAsync(Backup, 10_000_000);
        }

        string digest = Convert.ToHexStringLower(SHA256.HashData([1, 2, 3]));
        File.WriteAllBytes(ObjectPath(Backup, digest), [0, 0, 0]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LocalCatalog.RestoreAsync(Backup, Restored, 10_000_000));
        Assert.False(Directory.Exists(Restored));
        File.WriteAllBytes(ObjectPath(Backup, digest), [1, 2, 3]);

        File.WriteAllBytes(Path.Combine(Backup, "catalog.sqlite"), [1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LocalCatalog.VerifyBackupAsync(Backup, 10_000_000));
        Assert.False(Directory.Exists(Restored));
    }

    [Fact]
    public async Task IncompatibleManifestOrUnexpectedFilesRejectWithoutOverwrite()
    {
        using (LocalCatalog live = LocalCatalog.Open(Live))
        {
            await live.BackupAsync(Backup, 10_000_000);
        }

        string path = Path.Combine(Backup, "manifest.json");
        JsonObject json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json["FormatVersion"] = 99;
        File.WriteAllText(path, json.ToJsonString());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LocalCatalog.RestoreAsync(Backup, Restored, 10_000_000));
        Assert.False(Directory.Exists(Restored));

        json["FormatVersion"] = 1;
        File.WriteAllText(path, json.ToJsonString());
        File.WriteAllText(Path.Combine(Backup, "extra.txt"), "unexpected");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LocalCatalog.VerifyBackupAsync(Backup, 10_000_000));
    }

    [Fact]
    public async Task ExistingDestinationAndLowBudgetNeverOverwriteData()
    {
        using (LocalCatalog live = LocalCatalog.Open(Live))
        {
            await live.BackupAsync(Backup, 10_000_000);
        }

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            LocalCatalog.VerifyBackupAsync(Backup, 1));
        Directory.CreateDirectory(Restored);
        File.WriteAllText(Path.Combine(Restored, "important.txt"), "keep");
        await Assert.ThrowsAsync<IOException>(() =>
            LocalCatalog.RestoreAsync(Backup, Restored, 10_000_000));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(Restored, "important.txt")));
    }

    [Fact]
    public async Task CancelledSnapshotDoesNotCreateDestination()
    {
        using LocalCatalog live = LocalCatalog.Open(Live);
        using CancellationTokenSource cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            live.BackupAsync(Backup, 10_000_000, cancel.Token));
        Assert.False(Directory.Exists(Backup));
        Assert.Empty(Directory.GetDirectories(workspace, ".snapshot.incomplete-*"));
    }

    [Fact]
    public async Task PendingRequestsRemainInertAfterRestore()
    {
        using (LocalCatalog live = LocalCatalog.Open(Live))
        {
            using MemoryStream data = new MemoryStream([1]);
            CapturedOriginal captured = await live.CaptureFileAsync(Source("a"), data, 10, "done");
            using SqliteConnection db = Connect(live.DatabasePath);
            using SqliteCommand command = db.CreateCommand();
            command.CommandText = """
                INSERT INTO ingest_requests (request_key, source_id, state, attempt, failure_code, revision)
                VALUES ('queued', $source, 'queued', 0, NULL, NULL)
                """;
            command.Parameters.AddWithValue("$source", captured.SourceId.ToString("D"));
            command.ExecuteNonQuery();
            await live.BackupAsync(Backup, 10_000_000);
        }

        await LocalCatalog.RestoreAsync(Backup, Restored, 10_000_000);
        using LocalCatalog catalog = LocalCatalog.Open(Restored);
        using SqliteConnection db2 = Connect(catalog.DatabasePath);
        using SqliteCommand check = db2.CreateCommand();
        check.CommandText = "SELECT state FROM ingest_requests WHERE request_key='queued'";
        Assert.Equal("queued", check.ExecuteScalar());
        using ThrowingReadStream unused = new ThrowingReadStream();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.CaptureFileAsync(Source("a"), unused, 10, "queued"));
    }

    private static string ObjectPath(string root, string digest) =>
        Path.Combine(root, "objects", digest[..2], digest[2..4], digest);

    private static SqliteConnection Connect(string path)
    {
        SqliteConnection connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static long Count(SqliteConnection connection, string table)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
    }

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
            Directory.Delete(workspace, recursive: true);
        }
    }

    private sealed class ThrowingReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new InvalidOperationException("A completed receipt must not read input.");
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A completed receipt must not read input.");
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
