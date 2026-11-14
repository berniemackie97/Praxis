using System.Text;
using Praxis.Domain.Enrichment;
using Praxis.Domain.Library;
using Praxis.Domain.Originals;
using Praxis.Domain.Sources;
using Praxis.Infrastructure.FileSystem;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Intake;

/// <summary>
/// One file has one source identity, however the path to it is spelled: a folder reached
/// through a symbolic link (as macOS /tmp and /var are) must not create a second source.
/// </summary>
public sealed class CanonicalLocatorTests : IDisposable
{
    private readonly string workspace = Path.Combine(TestPaths.Temp, "praxis-locators-" + Guid.NewGuid().ToString("N"));
    private readonly string dataRoot;
    private readonly string realInbox;
    private readonly string aliasInbox;

    public CanonicalLocatorTests()
    {
        dataRoot = Path.Combine(workspace, "data");
        string real = Path.Combine(workspace, "real");
        realInbox = Path.Combine(real, "inbox");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(realInbox);
        Directory.CreateSymbolicLink(Path.Combine(workspace, "alias"), real);
        aliasInbox = Path.Combine(workspace, "alias", "inbox");
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(workspace, mode);
            File.SetUnixFileMode(dataRoot, mode);
        }
    }

    [Fact]
    public void CanonicalFormResolvesLinkedAncestorsAndKeepsMissingRemainder()
    {
        Assert.Equal(Path.Combine(realInbox, "note.md"), FileSystemPaths.Canonicalize(Path.Combine(aliasInbox, "note.md")));
        Assert.Equal(Path.Combine(realInbox, "later", "x.md"), FileSystemPaths.Canonicalize(Path.Combine(aliasInbox, "later", "x.md")));
        Assert.Equal(realInbox, FileSystemPaths.Canonicalize(realInbox));
        Assert.Equal(realInbox, FileSystemPaths.Canonicalize(aliasInbox + Path.DirectorySeparatorChar));
    }

    [Fact]
    public async Task WatchingTheSameFolderThroughEitherSpellingCapturesOnce()
    {
        File.WriteAllText(Path.Combine(realInbox, "plan.md"), "# Plan\nviolet");
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        DateTimeOffset time = DateTimeOffset.UtcNow;

        IntakeHarness throughAlias = new(catalog, aliasInbox, TimeSpan.Zero);
        await throughAlias.ReconcileAsync(time);
        await throughAlias.ReconcileAsync(time.AddSeconds(1));
        IntakeHarness throughReal = new(catalog, realInbox, TimeSpan.Zero);
        await throughReal.ReconcileAsync(time.AddSeconds(2));
        await throughReal.ReconcileAsync(time.AddSeconds(3));

        LibraryItem item = Assert.Single(catalog.Library.List());
        Assert.Equal(Path.Combine(realInbox, "plan.md"), item.SourceLocator);
        Assert.Single(catalog.Search.Query("violet"));
    }

    [Fact]
    public async Task ExistingSourcesUnderAnAliasSpellingAreRelocatedNotRecaptured()
    {
        string file = Path.Combine(realInbox, "kept.md");
        File.WriteAllText(file, "# Kept\nindigo");
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        CapturedOriginal recorded = await Capture(catalog, Path.Combine(aliasInbox, "kept.md"), "# Kept\nindigo");

        IntakeHarness intake = new(catalog, realInbox, TimeSpan.Zero);
        DateTimeOffset time = DateTimeOffset.UtcNow;
        await intake.ReconcileAsync(time);
        await intake.ReconcileAsync(time.AddSeconds(1));

        LibraryItem item = Assert.Single(catalog.Library.List());
        Assert.Equal((recorded.SourceId, file, 1), (item.SourceId, item.SourceLocator, item.Revision));
        SourceMovement move = Assert.Single(catalog.Sources.ListMovements(recorded.SourceId));
        Assert.Equal((Path.Combine(aliasInbox, "kept.md"), file), (move.PreviousPath, move.CurrentPath));
    }

    [Fact]
    public async Task DuplicateSpellingsFoldIntoOneSourceKeepingCollectionsAndReviews()
    {
        const string text = "# Architecture\nBody";
        string file = Path.Combine(realInbox, "arch.md");
        File.WriteAllText(file, text);
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        CapturedOriginal alias = await Capture(catalog, Path.Combine(aliasInbox, "arch.md"), text);
        CapturedOriginal canonical = await Capture(catalog, file, text);
        while (await ProcessingDriver.Text(catalog).ProcessNextAsync())
        {
        }

        await ProcessingDriver.SuggestHeadingTopicsAsync(catalog);

        Guid collection = catalog.Collections.Create("Design").CollectionId;
        Assert.True(catalog.Collections.AddSource(collection, alias.SourceId));
        EnrichmentProposal aliasHeading = Assert.Single(catalog.Proposals.List(alias.SourceId));
        Assert.True(catalog.Proposals.Review(aliasHeading.ProposalId, "corrected", "System architecture"));

        IntakeHarness intake = new(catalog, realInbox, TimeSpan.Zero);
        await intake.ReconcileAsync(DateTimeOffset.UtcNow);

        LibraryItem item = Assert.Single(catalog.Library.List(), entry => entry.SourceStatus == "active");
        Assert.Equal(canonical.SourceId, item.SourceId);
        Assert.Contains(collection, catalog.Collections.ListForSource(canonical.SourceId));
        EnrichmentProposal carried = Assert.Single(catalog.Proposals.List(canonical.SourceId));
        Assert.Equal(("corrected", "System architecture"), (carried.Status, carried.Correction));
        Assert.Empty(catalog.Proposals.List(alias.SourceId));
        Assert.Single(catalog.Search.Query("Body"));
    }

    [Fact]
    public async Task AnAliasOfAnOwnerRevokedSourceIsRevokedToo()
    {
        const string text = "# Private\nsecret";
        string file = Path.Combine(realInbox, "private.md");
        File.WriteAllText(file, text);
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        CapturedOriginal alias = await Capture(catalog, Path.Combine(aliasInbox, "private.md"), text);
        CapturedOriginal canonical = await Capture(catalog, file, text);
        Assert.True(catalog.Sources.Revoke(canonical.SourceId));

        await new IntakeHarness(catalog, realInbox, TimeSpan.Zero).ReconcileAsync(DateTimeOffset.UtcNow);

        Assert.Equal("revoked", catalog.Sources.FindLatestFile(Path.Combine(aliasInbox, "private.md"))?.Status);
        Assert.DoesNotContain(catalog.Library.List(), entry => entry.SourceStatus == "active");
    }

    private static async Task<CapturedOriginal> Capture(SqliteCatalog catalog, string locator, string text)
    {
        using MemoryStream stream = new(Encoding.UTF8.GetBytes(text));
        return await catalog.Originals.CaptureFileAsync(locator, stream, 10_000, Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
