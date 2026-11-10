using System.Text;
using Praxis.Storage.Catalog;
using Praxis.Storage.Intake;
using Praxis.Storage.Library;
using Praxis.Storage.Search;
using Praxis.Storage.Tests.Support;
using Xunit;

namespace Praxis.Storage.Tests.Intake;

public sealed class LocalQuickCaptureTests : IDisposable
{
    private readonly string workspace = Path.Combine(TestPaths.Temp,
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
            UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(workspace, mode);
            File.SetUnixFileMode(dataRoot, mode);
            File.SetUnixFileMode(inbox, mode);
        }
    }

    [Fact]
    public async Task QuickNoteIsDurableSourceWithSearchableAttributedContent()
    {
        Guid id = Guid.NewGuid();
        LocalQuickCaptureReceipt receipt;
        using (LocalCatalog catalog = LocalCatalog.Open(dataRoot))
        {
            LocalQuickCapture capture = new(catalog, inbox);
            receipt = await capture.CaptureAsync(id, "Project Ideas", "The violet compass.");
            Assert.Equal(Path.Combine(inbox, "Project-Ideas-" + id.ToString("N") + ".md"),
                receipt.SourcePath);
            Assert.Equal("# Project Ideas\n\nThe violet compass.",
                File.ReadAllText(receipt.SourcePath));
            Assert.Equal(receipt.Original.ItemId, Assert.Single(catalog.Library.List()).ItemId);
            Assert.Equal("file", Assert.Single(catalog.Library.List()).SourceKind);
            Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
            LocalSearchHit hit = Assert.Single(catalog.Search.Query("violet compass"));
            Assert.Equal(receipt.Original.SourceId, hit.SourceId);
            Assert.Equal(receipt.Original.ItemId, hit.ItemId);
            Assert.Equal(receipt.Original.Sha256, hit.OriginalSha256);
            await catalog.Originals.VerifyAsync(receipt.Original.ItemId);
            Assert.Empty(Directory.EnumerateFiles(inbox, "*.part", SearchOption.TopDirectoryOnly));
        }

        using LocalCatalog reopened = LocalCatalog.Open(dataRoot);
        LocalLibraryItem item = Assert.Single(reopened.Library.List());
        Assert.Equal(receipt.Original.ItemId, item.ItemId);
        Assert.Single(reopened.Search.Query("violet"));
        Assert.Equal(receipt.Original, (await new LocalQuickCapture(reopened, inbox)
            .CaptureAsync(id, "Project Ideas", "The violet compass.")).Original);
        await reopened.Originals.VerifyAsync(item.ItemId);
        Assert.Single(Directory.EnumerateFiles(inbox, "*.md", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task IdenticalRetriesNeverMakeAnotherNoteOrRevision()
    {
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        LocalQuickCapture capture = new(catalog, inbox);
        Guid id = Guid.NewGuid();
        LocalQuickCaptureReceipt first = await capture.CaptureAsync(id, null, "Original thought");
        LocalQuickCaptureReceipt second = await capture.CaptureAsync(id, null, "Original thought");
        Assert.Equal(first, second);
        Assert.Equal("Original thought", File.ReadAllText(first.SourcePath));
        IntakeHarness watcher = new(catalog, inbox, TimeSpan.Zero);
        DateTimeOffset time = DateTimeOffset.UtcNow;
        Assert.Equal(0, await watcher.ReconcileAsync(time));
        Assert.Equal(0, await watcher.ReconcileAsync(time.AddSeconds(1)));
        Assert.Single(catalog.Library.List());
        Assert.Equal(1, Assert.Single(catalog.Library.List()).Revision);
        Assert.Single(catalog.Search.Query("thought"));
        Assert.Single(Directory.EnumerateFiles(inbox, "*.md"));
    }

    [Fact]
    public async Task ChangedRetryCannotOverwriteOrDuplicateAnAcceptedNote()
    {
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        LocalQuickCapture capture = new(catalog, inbox);
        Guid id = Guid.NewGuid();
        LocalQuickCaptureReceipt original = await capture.CaptureAsync(id, "Research", "This text is preserved.");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            capture.CaptureAsync(id, "Research", "Different body"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            capture.CaptureAsync(id, "Different Title", "This text is preserved."));
        Assert.Equal("# Research\n\nThis text is preserved.", File.ReadAllText(original.SourcePath));
        Assert.Single(Directory.EnumerateFiles(inbox, "*.md"));
        Assert.Single(catalog.Library.List());
        await catalog.Originals.VerifyAsync(original.Original.ItemId);
    }

    [Fact]
    public async Task PublishedButUncommittedNoteIsRecoveredByTheExistingIntake()
    {
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        Guid id = Guid.NewGuid();
        string target = Path.Combine(inbox, "Recover-" + id.ToString("N") + ".md");
        File.WriteAllText(target, "# Recover\n\nOffline thought");
        LocalQuickCaptureReceipt result = await new LocalQuickCapture(catalog, inbox)
            .CaptureAsync(id, "Recover", "Offline thought");
        Assert.Equal(target, result.SourcePath);
        Assert.Equal(result.Original.ItemId, Assert.Single(catalog.Library.List()).ItemId);
        await catalog.Originals.VerifyAsync(result.Original.ItemId);
    }

    [Fact]
    public async Task ReplacedExistingContentIsNotOverwrittenOnRetry()
    {
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        Guid id = Guid.NewGuid();
        LocalQuickCapture capture = new(catalog, inbox);
        LocalQuickCaptureReceipt saved = await capture.CaptureAsync(id, "Topic", "Keep these bytes.");
        File.WriteAllText(saved.SourcePath, "external revision");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            capture.CaptureAsync(id, "Topic", "Keep these bytes."));
        Assert.Equal("external revision", File.ReadAllText(saved.SourcePath));
        Assert.Equal(1, Assert.Single(catalog.Library.List()).Revision);
        await catalog.Originals.VerifyAsync(saved.Original.ItemId);
    }

    [Fact]
    public async Task RejectedInputsCannotWriteAnInboxFile()
    {
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        LocalQuickCapture capture = new(catalog, inbox);
        Guid id = Guid.NewGuid();
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
        Assert.Empty(catalog.Library.List());
    }

    [Fact]
    public async Task CatalogAndLinkedRootsAreNotValidCaptureDestinations()
    {
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new LocalQuickCapture(catalog, dataRoot)
                .CaptureAsync(Guid.NewGuid(), "Private", "Cannot write into the catalog."));
        Assert.Empty(catalog.Library.List());

        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string link = Path.Combine(workspace, "linked-inbox");
        Directory.CreateSymbolicLink(link, inbox);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new LocalQuickCapture(catalog, link)
                .CaptureAsync(Guid.NewGuid(), "Linked", "Cannot follow a linked root."));
        Assert.Empty(Directory.EnumerateFiles(inbox));
    }

    [Fact]
    public async Task CancellationBeforeCaptureLeavesNoOriginalOrReceipt()
    {
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        using CancellationTokenSource cts = new();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new LocalQuickCapture(catalog, inbox)
                .CaptureAsync(Guid.NewGuid(), "Cancelled", "text", cts.Token));
        Assert.Empty(catalog.Library.List());
        Assert.Empty(Directory.EnumerateFiles(inbox));
    }

    [Fact]
    public async Task OwnerPastedUrlIsOnlyPreservedAsText()
    {
        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        string url = "https://example.invalid/private?token=abc";
        LocalQuickCaptureReceipt saved = await new LocalQuickCapture(catalog, inbox)
            .CaptureAsync(Guid.NewGuid(), "Reference", url);
        Assert.Equal("# Reference\n\n" + url, File.ReadAllText(saved.SourcePath));
        Assert.Equal("file", Assert.Single(catalog.Library.List()).SourceKind);
        await catalog.Originals.VerifyAsync(saved.Original.ItemId);
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
