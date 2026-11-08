using Microsoft.Data.Sqlite;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class LocalSourceIntakeTests : IDisposable
{
    private readonly string workspace = Path.Combine(Path.GetTempPath(),
        "praxis-intake-" + Guid.NewGuid().ToString("N"));
    private readonly string dataRoot;

    public LocalSourceIntakeTests()
    {
        dataRoot = Path.Combine(workspace, "store");
        Directory.CreateDirectory(dataRoot);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(dataRoot,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public async Task SelectedFolderPreservesNestedOriginalsAndStableSourceVersions()
    {
        var selected = Path.Combine(workspace, "selected");
        var nested = Path.Combine(selected, "notes");
        Directory.CreateDirectory(nested);
        var note = Path.Combine(nested, "idea.md");
        var binary = Path.Combine(selected, "unknown.data");
        File.WriteAllText(note, "idea one");
        File.WriteAllBytes(binary, [0, 1, 2, 255]);

        using var catalog = LocalCatalog.Open(dataRoot);
        var intake = new LocalSourceIntake(catalog);
        var first = await intake.ImportFolderAsync(selected, "batch-one", 10, 3, 100, 1000);
        Assert.Equal(2, first.Count);
        Assert.Equal(new[] { note, binary }, first.Select(x => x.SourcePath));
        Assert.Equal(2, catalog.ListProcessingJobs().Count);
        foreach (var item in first)
        {
            await catalog.VerifyOriginalAsync(item.Original.ItemId);
        }

        var replay = await intake.ImportFolderAsync(selected, "batch-one", 10, 3, 100, 1000);
        Assert.Equal(first, replay);
        Assert.Equal(2, catalog.ListProcessingJobs().Count);
        File.WriteAllText(note, "idea two");
        var changed = await intake.ImportFolderAsync(selected, "batch-two", 10, 3, 100, 1000);
        var before = first.Single(x => x.SourcePath == note).Original;
        var after = changed.Single(x => x.SourcePath == note).Original;
        Assert.Equal(before.SourceId, after.SourceId);
        Assert.Equal(before.ItemId, after.ItemId);
        Assert.Equal(2, after.Revision);
        Assert.Equal(3, catalog.ListProcessingJobs().Count);
        Assert.Equal(1, changed.Single(x => x.SourcePath == binary).Original.Revision);
        Assert.Equal(new byte[] { 0, 1, 2, 255 },
            ReadObject(dataRoot, first.Single(x => x.SourcePath == binary).Original.Sha256));
        Assert.Equal("idea one", System.Text.Encoding.UTF8.GetString(
            ReadObject(dataRoot, before.Sha256)));
    }

    [Fact]
    public async Task ExplicitFileCaptureRetainsOriginalAfterSourceDisappears()
    {
        var file = Path.Combine(workspace, "note.txt");
        File.WriteAllBytes(file, [5, 6, 7]);
        CapturedOriginal first;
        using (var catalog = LocalCatalog.Open(dataRoot))
        {
            first = await new LocalSourceIntake(catalog)
                .ImportFileAsync(file, 3, "owner-file-one");
            Assert.Single(catalog.ListProcessingJobs());
        }

        File.Delete(file);
        using var reopened = LocalCatalog.Open(dataRoot);
        await reopened.VerifyOriginalAsync(first.ItemId);
        Assert.Equal(new byte[] { 5, 6, 7 }, ReadObject(dataRoot, first.Sha256));
    }

    [Fact]
    public async Task FolderQuotaFailsBeforeAnyOriginalIsCommitted()
    {
        var selected = Path.Combine(workspace, "input");
        Directory.CreateDirectory(selected);
        File.WriteAllBytes(Path.Combine(selected, "a"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(selected, "b"), [4, 5, 6]);
        using var catalog = LocalCatalog.Open(dataRoot);
        var intake = new LocalSourceIntake(catalog);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            intake.ImportFolderAsync(selected, "batch", 10, 1, 10, 4));
        Assert.Empty(catalog.ListProcessingJobs());
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            intake.ImportFolderAsync(selected, "batch", 1, 1, 10, 10));
        Assert.Empty(catalog.ListProcessingJobs());
        using var db = Connect(catalog.DatabasePath);
        Assert.Equal(0L, Count(db, "ingest_requests"));
    }

    [Fact]
    public async Task FolderCannotImportStoreItselfOrEscapeDepthBoundary()
    {
        var selected = Path.Combine(workspace, "selection");
        Directory.CreateDirectory(Path.Combine(selected, "nested"));
        File.WriteAllText(Path.Combine(selected, "nested", "a.txt"), "a");
        using var catalog = LocalCatalog.Open(dataRoot);
        var intake = new LocalSourceIntake(catalog);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            intake.ImportFolderAsync(dataRoot, "batch", 10, 2, 10, 100));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            intake.ImportFolderAsync(selected, "batch", 10, 0, 10, 100));
        Assert.Empty(catalog.ListProcessingJobs());
        Assert.Throws<ArgumentException>(() =>
            intake.ImportFileAsync("relative/file.txt", 10, "request").GetAwaiter().GetResult());
    }

    [Fact]
    public async Task SymbolicLinkInSelectedFolderIsRejectedWithoutFollowingIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var selected = Path.Combine(workspace, "selection");
        Directory.CreateDirectory(selected);
        var outside = Path.Combine(workspace, "outside.txt");
        File.WriteAllText(outside, "private");
        File.CreateSymbolicLink(Path.Combine(selected, "linked.txt"), outside);
        using var catalog = LocalCatalog.Open(dataRoot);
        var intake = new LocalSourceIntake(catalog);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            intake.ImportFolderAsync(selected, "batch", 10, 1, 100, 1000));
        Assert.Empty(catalog.ListProcessingJobs());
        Assert.Equal("private", File.ReadAllText(outside));
    }

    [Fact]
    public async Task CancellationAndOversizeSingleFileNeverWriteAReceipt()
    {
        var file = Path.Combine(workspace, "file.bin");
        File.WriteAllBytes(file, [1, 2, 3, 4]);
        using var catalog = LocalCatalog.Open(dataRoot);
        var intake = new LocalSourceIntake(catalog);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            intake.ImportFileAsync(file, 2, "too-large"));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            intake.ImportFileAsync(file, 10, "canceled", canceled.Token));
        using var db = Connect(catalog.DatabasePath);
        Assert.Equal(0L, Count(db, "ingest_requests"));
        Assert.Empty(catalog.ListProcessingJobs());
    }

    private static byte[] ReadObject(string root, string digest) =>
        File.ReadAllBytes(Path.Combine(root, "objects", digest[..2], digest[2..4], digest));

    private static SqliteConnection Connect(string database)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        db.Open();
        return db;
    }

    private static long Count(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table}";
        return (long)command.ExecuteScalar()!;
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
