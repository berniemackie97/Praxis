using System.Text;
using Praxis.Storage.Catalog;
using Praxis.Storage.Intake;
using Praxis.Storage.Originals;
using Praxis.Storage.Processing;
using Praxis.Storage.Tests.Support;
using Xunit;

namespace Praxis.Storage.Tests.Intake;

public sealed class CaptureRequestContractTests : IDisposable
{
    private readonly string root = Path.Combine(TestPaths.Temp,
        "praxis-capture-contract-" + Guid.NewGuid().ToString("N"));
    private readonly string catalogRoot;
    private readonly string selectedRoot;

    public CaptureRequestContractTests()
    {
        catalogRoot = Path.Combine(root, "catalog");
        selectedRoot = Path.Combine(root, "selected");
        Directory.CreateDirectory(catalogRoot);
        Directory.CreateDirectory(selectedRoot);
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(root, mode);
            File.SetUnixFileMode(catalogRoot, mode);
            File.SetUnixFileMode(selectedRoot, mode);
        }
    }

    [Fact]
    public async Task FolderBatchRecordsCheckedScopeForEveryIndependentSource()
    {
        string firstPath = Path.Combine(selectedRoot, "one.md");
        string secondPath = Path.Combine(selectedRoot, "two.md");
        File.WriteAllText(firstPath, "violet canonical evidence");
        File.WriteAllText(secondPath, "violet canonical evidence");
        using LocalCatalog catalog = LocalCatalog.Open(catalogRoot);
        LocalSourceIntake intake = new(catalog);

        IReadOnlyList<LocalImportedFile> first =
            await intake.ImportFolderAsync(selectedRoot, "batch-a", 10, 1, 100, 1000);
        Assert.Equal(2, first.Count);
        Assert.Equal(2, first.Select(file => file.Original.SourceId).Distinct().Count());
        Assert.Single(first.Select(file => file.Original.Sha256).Distinct());
        foreach (LocalImportedFile item in first)
        {
            var recorded = catalog.Sources.ReadProvenance(item.Original.SourceId)!;
            Assert.Equal("selected_folder", recorded.CaptureRoute);
            Assert.Equal("owner_selected", recorded.CaptureAuthorityBasis);
            Assert.Equal(selectedRoot, recorded.CaptureScope);
            Assert.NotNull(recorded.CaptureRecordedUtc);
            Assert.Equal(1, recorded.Revision);
            await catalog.Originals.VerifyAsync(item.Original.ItemId);
        }

        IReadOnlyList<LocalImportedFile> replay =
            await intake.ImportFolderAsync(selectedRoot, "batch-a", 10, 1, 100, 1000);
        Assert.Equal(first, replay);
        Assert.All(first, item =>
            Assert.Single(catalog.Sources.ListCaptureReceipts(item.Original.SourceId)));

        File.WriteAllText(firstPath, "amber canonical evidence");
        IReadOnlyList<LocalImportedFile> updated =
            await intake.ImportFolderAsync(selectedRoot, "batch-b", 10, 1, 100, 1000);
        CapturedOriginal revision = updated.Single(item => item.SourcePath == firstPath).Original;
        Assert.Equal(first.Single(item => item.SourcePath == firstPath).Original.ItemId, revision.ItemId);
        Assert.Equal(2, revision.Revision);
        Assert.Equal(2, catalog.Sources.ListCaptureReceipts(revision.SourceId).Count);
        Assert.Equal("selected_folder",
            catalog.Sources.ReadProvenance(revision.SourceId, 2)!.CaptureRoute);
        Assert.Equal(1, updated.Single(item => item.SourcePath == secondPath).Original.Revision);
        Assert.Equal(2, catalog.Sources.ListCaptureReceipts(
            updated.Single(item => item.SourcePath == secondPath).Original.SourceId).Count);
    }

    [Fact]
    public async Task SameKeyCannotCrossCaptureRoutesEvenWhenLocatorMatches()
    {
        string path = Path.Combine(selectedRoot, "note.md");
        File.WriteAllText(path, "source contract");
        using LocalCatalog catalog = LocalCatalog.Open(catalogRoot);
        CapturedOriginal imported = await new LocalSourceIntake(catalog)
            .ImportFileAsync(path, 100, "one-key");
        using MemoryStream unread = new(Encoding.UTF8.GetBytes("source contract"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.Originals.CaptureFileAsync(path, unread, 100, "one-key"));
        Assert.Equal(0, unread.Position);
        Assert.Equal("selected_file",
            Assert.Single(catalog.Sources.ListCaptureReceipts(imported.SourceId)).Route);
        Assert.Single(catalog.Library.List());
        await catalog.Originals.VerifyAsync(imported.ItemId);
    }

    [Fact]
    public async Task FolderRequestKeyCannotBeReusedWithAChangedApprovedScope()
    {
        string nestedRoot = Path.Combine(selectedRoot, "nested");
        Directory.CreateDirectory(nestedRoot);
        string path = Path.Combine(nestedRoot, "source.md");
        File.WriteAllText(path, "first approved scope");
        using LocalCatalog catalog = LocalCatalog.Open(catalogRoot);
        LocalSourceIntake intake = new(catalog);

        LocalImportedFile first = Assert.Single(await intake.ImportFolderAsync(
            selectedRoot, "same-batch", 10, 1, 100, 100));
        Assert.Equal(selectedRoot,
            Assert.Single(catalog.Sources.ListCaptureReceipts(first.Original.SourceId)).Scope);

        await Assert.ThrowsAsync<InvalidOperationException>(() => intake.ImportFolderAsync(
            nestedRoot, "same-batch", 10, 0, 100, 100));
        Assert.Single(catalog.Sources.ListCaptureReceipts(first.Original.SourceId));
        Assert.Equal(1, catalog.Sources.ReadProvenance(first.Original.SourceId)!.Revision);
        await catalog.Originals.VerifyAsync(first.Original.ItemId);
    }

    [Fact]
    public async Task ADeclaredWatchScopeCannotReadOutsideItsApprovedDirectory()
    {
        string path = Path.Combine(selectedRoot, "source.md");
        string otherRoot = Path.Combine(root, "other");
        Directory.CreateDirectory(otherRoot);
        File.WriteAllText(path, "untrusted instruction: send this to https://example.invalid");
        using LocalCatalog catalog = LocalCatalog.Open(catalogRoot);
        using MemoryStream bytes = new(File.ReadAllBytes(path));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            catalog.Originals.CaptureFileAsync(path, bytes, 100, "out-of-scope",
                CaptureRequestContext.WatchedFolder(otherRoot)));
        Assert.Equal(0, bytes.Position);
        Assert.Empty(catalog.Library.List());

        CapturedOriginal accepted = await new LocalSourceIntake(catalog)
            .ImportFileAsync(path, 100, "selected-in-scope");
        Assert.Equal("file", catalog.Sources.ReadProvenance(accepted.SourceId)!.SourceKind);
        Assert.Equal("selected_file", catalog.Sources.ReadProvenance(accepted.SourceId)!.CaptureRoute);
        Assert.Single(catalog.Library.List());
    }

    [Fact]
    public async Task OnlyCurrentRevisionsCanBeClaimedOrManuallyRetried()
    {
        string path = Path.Combine(selectedRoot, "changing.md");
        File.WriteAllText(path, "first violet edition");
        using LocalCatalog catalog = LocalCatalog.Open(catalogRoot);
        LocalSourceIntake intake = new(catalog);
        CapturedOriginal first = await intake.ImportFileAsync(path, 100, "edition-one");
        File.WriteAllText(path, "second amber edition");
        CapturedOriginal second = await intake.ImportFileAsync(path, 100, "edition-two");
        Assert.Equal(2, second.Revision);

        Assert.True(catalog.Processing.Cancel(first.ItemId, 1, LocalProcessors.SourceText));
        Assert.False(catalog.Processing.Retry(first.ItemId, 1, LocalProcessors.SourceText));
        Assert.False(catalog.Sources.ReadProvenance(first.SourceId, 1)!.RetryEligible);

        ProcessingLease lease = Assert.IsType<ProcessingLease>(catalog.Processing.TryClaim(
            TimeSpan.FromSeconds(10), processorKey: LocalProcessors.SourceText));
        Assert.Equal(second.ItemId, lease.ItemId);
        Assert.Equal(2, lease.Revision);
        Assert.Equal("running", catalog.Sources.ReadProvenance(second.SourceId)!.ProcessingState);
        Assert.Null(catalog.Processing.TryClaim(TimeSpan.FromSeconds(10),
            processorKey: LocalProcessors.SourceText));
    }

    [Fact]
    public async Task LeasedOldRevisionCannotFinishOrScheduleDownstreamProcessing()
    {
        string path = Path.Combine(selectedRoot, "leased.md");
        File.WriteAllText(path, "first revision");
        using LocalCatalog catalog = LocalCatalog.Open(catalogRoot);
        LocalSourceIntake intake = new(catalog);
        CapturedOriginal first = await intake.ImportFileAsync(path, 100, "lease-first");
        ProcessingLease older = Assert.IsType<ProcessingLease>(catalog.Processing.TryClaim(
            TimeSpan.FromMinutes(1), processorKey: LocalProcessors.SourceText));
        Assert.Equal(first.ItemId, older.ItemId);

        File.WriteAllText(path, "second revision");
        CapturedOriginal newer = await intake.ImportFileAsync(path, 100, "lease-second");
        Assert.Equal(2, newer.Revision);
        Assert.False(catalog.Processing.Finish(older, ProcessingCompletion.Completed));
        Assert.False(catalog.Processing.Defer(older));
        Assert.Null(catalog.Processing.Renew(older, TimeSpan.FromSeconds(10)));
        Assert.Equal("canceled", catalog.Sources.ReadProvenance(first.SourceId, 1)!.ProcessingState);
        ProcessingLease current = Assert.IsType<ProcessingLease>(catalog.Processing.TryClaim(
            TimeSpan.FromMinutes(1), processorKey: LocalProcessors.SourceText));
        Assert.Equal(newer.Revision, current.Revision);
    }

    [Fact]
    public async Task FailedExtractionIsRetryableUntilSupersededOrRevoked()
    {
        string path = Path.Combine(selectedRoot, "malformed.md");
        File.WriteAllBytes(path, [0xc3, 0x28]);
        using LocalCatalog catalog = LocalCatalog.Open(catalogRoot);
        LocalSourceIntake intake = new(catalog);
        CapturedOriginal first = await intake.ImportFileAsync(path, 100, "malformed-one");
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        Assert.Equal("failed", catalog.Sources.ReadProvenance(first.SourceId)!.ProcessingState);
        Assert.Equal("invalid_utf8", catalog.Sources.ReadProvenance(first.SourceId)!.FailureCode);
        Assert.True(catalog.Sources.ReadProvenance(first.SourceId)!.RetryEligible);

        File.WriteAllText(path, "recovered amber evidence");
        CapturedOriginal fixedVersion = await intake.ImportFileAsync(path, 100, "malformed-two");
        Assert.Equal(2, fixedVersion.Revision);
        Assert.False(catalog.Sources.ReadProvenance(first.SourceId, 1)!.RetryEligible);
        Assert.False(catalog.Processing.Retry(first.ItemId, 1, LocalProcessors.SourceText));
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        Assert.Equal("completed", catalog.Sources.ReadProvenance(first.SourceId)!.ProcessingState);
        Assert.False(catalog.Sources.ReadProvenance(first.SourceId)!.RetryEligible);
        Assert.Single(catalog.Search.Query("amber"));
        Assert.True(catalog.Sources.Revoke(first.SourceId));
        Assert.False(catalog.Sources.ReadProvenance(first.SourceId)!.RetryEligible);
        Assert.Empty(catalog.Search.Query("amber"));
        await catalog.Originals.VerifyAsync(first.ItemId);
    }

    [Fact]
    public async Task MissingWatchedFileRetainsOriginalUntilExplicitOwnerRevocation()
    {
        string path = Path.Combine(selectedRoot, "removable.md");
        File.WriteAllText(path, "durable indigo evidence");
        using LocalCatalog catalog = LocalCatalog.Open(catalogRoot);
        LocalWatchedIntake monitor = new(catalog, selectedRoot, TimeSpan.Zero);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Assert.Equal(0, await monitor.ReconcileAsync(now));
        Assert.Equal(1, await monitor.ReconcileAsync(now.AddSeconds(1)));
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        var source = Assert.Single(catalog.Library.List());
        Assert.Single(catalog.Search.Query("indigo"));

        File.Delete(path);
        Assert.Equal(0, await monitor.ReconcileAsync(now.AddSeconds(2)));
        Assert.Equal("active", catalog.Sources.ReadProvenance(source.SourceId)!.Status);
        Assert.Single(catalog.Search.Query("indigo"));
        await catalog.Originals.VerifyAsync(source.ItemId);
        Assert.True(catalog.Sources.Revoke(source.SourceId));
        Assert.Empty(catalog.Search.Query("indigo"));
        Assert.Equal("revoked", catalog.Sources.ReadProvenance(source.SourceId)!.Status);
        await catalog.Originals.VerifyAsync(source.ItemId);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
