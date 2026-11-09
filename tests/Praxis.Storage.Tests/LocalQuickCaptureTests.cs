using System.Text;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class LocalQuickCaptureTests : IDisposable
{
    private readonly string workspace = Path.Combine(Path.GetTempPath(),
        "praxis-quick-note-" + Guid.NewGuid().ToString("N"));
    private readonly string dataRoot;
    private readonly string inbox;

    public LocalQuickCaptureTests()
    {
        dataRoot = Path.Combine(workspace, "data");
        inbox = Path.Combine(workspace, "inbox");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(inbox);
        if (!OperatingSystem.IsWindows())
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(workspace, mode);
            File.SetUnixFileMode(dataRoot, mode);
            File.SetUnixFileMode(inbox, mode);
        }
    }

    [Fact]
    public async Task QuickNoteIsDurableSourceWithSearchableAttributedContent()
    {
        var id = Guid.NewGuid();
        LocalQuickCaptureReceipt receipt;
        using (var catalog = LocalCatalog.Open(dataRoot))
        {
            var capture = new LocalQuickCapture(catalog, inbox);
            receipt = await capture.CaptureAsync(id, "Project Ideas", "The violet compass.");
            Assert.Equal(Path.Combine(inbox, "Project-Ideas-" + id.ToString("N") + ".md"),
                receipt.SourcePath);
            Assert.Equal("# Project Ideas\n\nThe violet compass.",
                File.ReadAllText(receipt.SourcePath));
            Assert.Equal(receipt.Original.ItemId, Assert.Single(catalog.ListLibrary()).ItemId);
            Assert.Equal("file", Assert.Single(catalog.ListLibrary()).SourceKind);
            Assert.True(await new LocalTextProcessor(catalog).ProcessNextAsync());
            var hit = Assert.Single(catalog.SearchText("violet compass"));
            Assert.Equal(receipt.Original.SourceId, hit.SourceId);
            Assert.Equal(receipt.Original.ItemId, hit.ItemId);
            Assert.Equal(receipt.Original.Sha256, hit.OriginalSha256);
            await catalog.VerifyOriginalAsync(receipt.Original.ItemId);
            Assert.Empty(Directory.EnumerateFiles(inbox, "*.part", SearchOption.TopDirectoryOnly));
        }

        using var reopened = LocalCatalog.Open(dataRoot);
        var item = Assert.Single(reopened.ListLibrary());
        Assert.Equal(receipt.Original.ItemId, item.ItemId);
        Assert.Single(reopened.SearchText("violet"));
        Assert.Equal(receipt.Original, (await new LocalQuickCapture(reopened, inbox)
            .CaptureAsync(id, "Project Ideas", "The violet compass.")).Original);
        await reopened.VerifyOriginalAsync(item.ItemId);
        Assert.Single(Directory.EnumerateFiles(inbox, "*.md", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task IdenticalRetriesNeverMakeAnotherNoteOrRevision()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var capture = new LocalQuickCapture(catalog, inbox);
        var id = Guid.NewGuid();
        var first = await capture.CaptureAsync(id, null, "Original thought");
        var second = await capture.CaptureAsync(id, null, "Original thought");
        Assert.Equal(first, second);
        Assert.Equal("Original thought", File.ReadAllText(first.SourcePath));
        var watcher = new LocalWatchedIntake(catalog, inbox, TimeSpan.Zero);
        var time = DateTimeOffset.UtcNow;
        Assert.Equal(0, await watcher.ReconcileAsync(time));
        Assert.Equal(0, await watcher.ReconcileAsync(time.AddSeconds(1)));
        Assert.Single(catalog.ListLibrary());
        Assert.Equal(1, Assert.Single(catalog.ListLibrary()).Revision);
        Assert.Single(catalog.SearchText("thought"));
        Assert.Single(Directory.EnumerateFiles(inbox, "*.md"));
    }

    [Fact]
    public async Task ChangedRetryCannotOverwriteOrDuplicateAnAcceptedNote()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var capture = new LocalQuickCapture(catalog, inbox);
        var id = Guid.NewGuid();
        var original = await capture.CaptureAsync(id, "Research", "This text is preserved.");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            capture.CaptureAsync(id, "Research", "Different body"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            capture.CaptureAsync(id, "Different Title", "This text is preserved."));
        Assert.Equal("# Research\n\nThis text is preserved.", File.ReadAllText(original.SourcePath));
        Assert.Single(Directory.EnumerateFiles(inbox, "*.md"));
        Assert.Single(catalog.ListLibrary());
        await catalog.VerifyOriginalAsync(original.Original.ItemId);
    }

    [Fact]
    public async Task PublishedButUncommittedNoteIsRecoveredByTheExistingIntake()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var id = Guid.NewGuid();
        var target = Path.Combine(inbox, "Recover-" + id.ToString("N") + ".md");
        File.WriteAllText(target, "# Recover\n\nOffline thought");
        var result = await new LocalQuickCapture(catalog, inbox)
            .CaptureAsync(id, "Recover", "Offline thought");
        Assert.Equal(target, result.SourcePath);
        Assert.Equal(result.Original.ItemId, Assert.Single(catalog.ListLibrary()).ItemId);
        await catalog.VerifyOriginalAsync(result.Original.ItemId);
    }

    [Fact]
    public async Task ReplacedExistingContentIsNotOverwrittenOnRetry()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var id = Guid.NewGuid();
        var capture = new LocalQuickCapture(catalog, inbox);
        var saved = await capture.CaptureAsync(id, "Topic", "Keep these bytes.");
        File.WriteAllText(saved.SourcePath, "external revision");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            capture.CaptureAsync(id, "Topic", "Keep these bytes."));
        Assert.Equal("external revision", File.ReadAllText(saved.SourcePath));
        Assert.Equal(1, Assert.Single(catalog.ListLibrary()).Revision);
        await catalog.VerifyOriginalAsync(saved.Original.ItemId);
    }

    [Fact]
    public async Task RejectedInputsCannotWriteAnInboxFile()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var capture = new LocalQuickCapture(catalog, inbox);
        var id = Guid.NewGuid();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            capture.CaptureAsync(Guid.Empty, "Notes", "hello"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            capture.CaptureAsync(id, "Notes", "  "));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            capture.CaptureAsync(id, "Bad\nTitle", "hello"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            capture.CaptureAsync(id, new string('x', 81), "hello"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            capture.CaptureAsync(id, "Notes", new string('x', 8_001)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            capture.CaptureAsync(id, "Notes", "hello\0world"));
        await Assert.ThrowsAsync<EncoderFallbackException>(() =>
            capture.CaptureAsync(id, "Notes", "invalid \uD800"));
        Assert.Empty(Directory.EnumerateFiles(inbox));
        Assert.Empty(catalog.ListLibrary());
    }

    [Fact]
    public async Task CatalogAndLinkedRootsAreNotValidCaptureDestinations()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LocalQuickCapture(catalog, dataRoot)
                .CaptureAsync(Guid.NewGuid(), "Private", "Cannot write into the catalog."));
        Assert.Empty(catalog.ListLibrary());

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var link = Path.Combine(workspace, "linked-inbox");
        Directory.CreateSymbolicLink(link, inbox);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LocalQuickCapture(catalog, link)
                .CaptureAsync(Guid.NewGuid(), "Linked", "Cannot follow a linked root."));
        Assert.Empty(Directory.EnumerateFiles(inbox));
    }

    [Fact]
    public async Task CancellationBeforeCaptureLeavesNoOriginalOrReceipt()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalQuickCapture(catalog, inbox)
                .CaptureAsync(Guid.NewGuid(), "Cancelled", "text", cts.Token));
        Assert.Empty(catalog.ListLibrary());
        Assert.Empty(Directory.EnumerateFiles(inbox));
    }

    [Fact]
    public async Task OwnerPastedUrlIsOnlyPreservedAsText()
    {
        using var catalog = LocalCatalog.Open(dataRoot);
        var url = "https://example.invalid/private?token=abc";
        var saved = await new LocalQuickCapture(catalog, inbox)
            .CaptureAsync(Guid.NewGuid(), "Reference", url);
        Assert.Equal("# Reference\n\n" + url, File.ReadAllText(saved.SourcePath));
        Assert.Equal("file", Assert.Single(catalog.ListLibrary()).SourceKind);
        await catalog.VerifyOriginalAsync(saved.Original.ItemId);
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
