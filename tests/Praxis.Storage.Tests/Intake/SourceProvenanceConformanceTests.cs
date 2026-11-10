using System.Text;
using Microsoft.Data.Sqlite;
using Praxis.Storage.Catalog;
using Praxis.Storage.Intake;
using Praxis.Storage.Originals;
using Praxis.Storage.Processing;
using Praxis.Storage.Sources;
using Praxis.Storage.Tests.Support;
using Xunit;

namespace Praxis.Storage.Tests.Intake;

public sealed class SourceProvenanceConformanceTests : IDisposable
{
    private readonly string workspace = Path.Combine(TestPaths.Temp,
        "praxis-source-conformance-" + Guid.NewGuid().ToString("N"));
    private readonly string dataRoot;
    private readonly string inbox;
    private readonly string manualRoot;

    public SourceProvenanceConformanceTests()
    {
        dataRoot = Path.Combine(workspace, "data");
        inbox = Path.Combine(workspace, "inbox");
        manualRoot = Path.Combine(workspace, "manual");
        Directory.CreateDirectory(dataRoot);
        Directory.CreateDirectory(inbox);
        Directory.CreateDirectory(manualRoot);
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(workspace, mode);
            File.SetUnixFileMode(dataRoot, mode);
            File.SetUnixFileMode(inbox, mode);
            File.SetUnixFileMode(manualRoot, mode);
        }
    }

    [Fact]
    public async Task ManualQuickAndWatchedSourcesShareRevisionContractButNotOwnership()
    {
        const string sharedText = "# Shared\n\nviolet blueprint";
        string manualPath = Path.Combine(manualRoot, "shared.md");
        string watchedPath = Path.Combine(inbox, "watched.md");
        File.WriteAllText(manualPath, sharedText);
        File.WriteAllText(watchedPath, "# Watching\nivy orchard");

        CapturedOriginal manual;
        CapturedOriginal quick;
        Guid watchedSourceId;
        using (LocalCatalog catalog = LocalCatalog.Open(dataRoot))
        {
            LocalSourceIntake intake = new(catalog);
            manual = await intake.ImportFileAsync(manualPath,
                Encoding.UTF8.GetByteCount(sharedText), "manual-request-1");
            Guid captureId = Guid.NewGuid();
            LocalQuickCaptureReceipt quickReceipt = await new LocalQuickCapture(catalog, inbox)
                .CaptureAsync(captureId, "Shared", "violet blueprint");
            quick = quickReceipt.Original;

            LocalWatchedIntake watcher = new(catalog, inbox, TimeSpan.Zero);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Assert.Equal(0, await watcher.ReconcileAsync(now));
            Assert.Equal(1, await watcher.ReconcileAsync(now.AddSeconds(1)));
            Assert.Equal(0, await watcher.ReconcileAsync(now.AddSeconds(2)));

            LocalFileVersion watched = catalog.Sources.FindLatestFile(watchedPath)!;
            watchedSourceId = watched.SourceId;
            Assert.Equal(1, watched.Revision);
            Assert.Equal(3, catalog.Library.List().Count);
            Assert.Equal(2L, Count(catalog.DatabasePath, "original_objects"));
            Assert.Equal(3L, Count(catalog.DatabasePath, "ingest_requests"));
            Assert.Equal(manual.Sha256, quick.Sha256);
            Assert.NotEqual(manual.SourceId, quick.SourceId);
            Assert.NotEqual(manual.ItemId, quick.ItemId);
            Assert.NotEqual(manual.SourceId, watchedSourceId);

            for (int index = 0; index < 3; index++)
            {
                Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
            }

            Assert.All(new[] { manual.SourceId, quick.SourceId, watchedSourceId },
                source => AssertSource(catalog.Sources.ReadProvenance(source)!));
            AssertCapture(catalog, manual.SourceId, "selected_file", "owner_selected", manualPath);
            AssertCapture(catalog, quick.SourceId, "quick_note", "owner_submitted", inbox);
            AssertCapture(catalog, watchedSourceId, "watched_folder", "owner_configured", inbox);
            Assert.Equal(2, catalog.Search.Query("violet blueprint").Count);
            Assert.Single(catalog.Search.Query("ivy orchard"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                intake.ImportFileAsync(quickReceipt.SourcePath, quick.Length, "manual-request-1"));

            File.WriteAllText(manualPath, "amber blueprint");
            CapturedOriginal updated = await intake.ImportFileAsync(manualPath, 100, "manual-request-2");
            Assert.Equal(manual.SourceId, updated.SourceId);
            Assert.Equal(manual.ItemId, updated.ItemId);
            Assert.Equal(2, updated.Revision);
            Assert.NotEqual(manual.Sha256, updated.Sha256);
            Assert.Equal(2, catalog.Sources.ListCaptureReceipts(manual.SourceId).Count);
            Assert.Equal(2, catalog.Sources.ListCaptureReceipts(manual.SourceId, 2).Single().Revision);
            Assert.Equal("selected_file", catalog.Sources.ReadProvenance(manual.SourceId, 2)!.CaptureRoute);
            Assert.Equal(manual.Sha256, catalog.Sources.ReadProvenance(manual.SourceId, 1)!.OriginalSha256);
            Assert.Equal("queued", catalog.Sources.ReadProvenance(manual.SourceId)!.ProcessingState);
            Assert.Equal(quick.SourceId, Assert.Single(catalog.Search.Query("violet")).SourceId);
            Assert.Empty(catalog.Search.Query("amber"));
            Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
            Assert.Equal(updated.SourceId, Assert.Single(catalog.Search.Query("amber")).SourceId);

            Assert.True(catalog.Sources.Revoke(quick.SourceId));
            Assert.Equal("revoked", catalog.Sources.ReadProvenance(quick.SourceId)!.Status);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                new LocalQuickCapture(catalog, inbox).CaptureAsync(captureId, "Shared", "violet blueprint"));
            Assert.Empty(catalog.Search.Query("violet"));
            Assert.Equal(2, catalog.Search.Rebuild());
            Assert.Equal(updated.SourceId, Assert.Single(catalog.Search.Query("amber")).SourceId);
            Assert.Equal(watchedSourceId, Assert.Single(catalog.Search.Query("ivy")).SourceId);
            await catalog.Originals.VerifyAsync(quick.ItemId);
            await catalog.Originals.VerifyAsync(updated.ItemId);
        }

        using LocalCatalog reopened = LocalCatalog.Open(dataRoot);
        Assert.Equal("revoked", reopened.Sources.ReadProvenance(quick.SourceId)!.Status);
        Assert.Equal(manual.Sha256, reopened.Sources.ReadProvenance(manual.SourceId, 1)!.OriginalSha256);
        Assert.Equal(2, reopened.Sources.ReadProvenance(manual.SourceId)!.Revision);
        Assert.Equal(2, reopened.Search.Rebuild());
        Assert.Empty(reopened.Search.Query("violet"));
        Assert.Single(reopened.Search.Query("amber"));
    }

    [Fact]
    public async Task UnsupportedAndMalformedSourcesExposeTheirActualProcessingOutcomes()
    {
        string unsupportedPath = Path.Combine(manualRoot, "asset.unknown");
        string invalidPath = Path.Combine(manualRoot, "malformed.md");
        File.WriteAllBytes(unsupportedPath, [0, 255, 1, 2]);
        File.WriteAllBytes(invalidPath, [0xc3, 0x28]);

        using LocalCatalog catalog = LocalCatalog.Open(dataRoot);
        LocalSourceIntake intake = new(catalog);
        CapturedOriginal unsupported = await intake.ImportFileAsync(unsupportedPath, 4, "unknown-file");
        CapturedOriginal malformed = await intake.ImportFileAsync(invalidPath, 2, "invalid-utf8");
        Assert.Equal("queued", catalog.Sources.ReadProvenance(unsupported.SourceId)!.ProcessingState);
        Assert.Equal("queued", catalog.Sources.ReadProvenance(malformed.SourceId)!.ProcessingState);

        ProcessingDriver worker = ProcessingDriver.Text(catalog);
        Assert.True(await worker.ProcessNextAsync());
        Assert.True(await worker.ProcessNextAsync());

        LocalSourceProvenance unknown = catalog.Sources.ReadProvenance(unsupported.SourceId)!;
        LocalSourceProvenance invalid = catalog.Sources.ReadProvenance(malformed.SourceId)!;
        Assert.Equal("unsupported", unknown.ProcessingState);
        Assert.Equal("unsupported", unknown.ExtractionOutcome);
        Assert.Equal("unsupported_format", unknown.ExtractionReasonCode);
        Assert.Null(unknown.FailureCode);
        Assert.Equal("failed", invalid.ProcessingState);
        Assert.Equal("invalid_utf8", invalid.FailureCode);
        Assert.Null(invalid.ExtractionOutcome);
        Assert.True(invalid.RetryEligible);
        Assert.False(unknown.RetryEligible);
        LocalSourceProcessingState failed = Assert.Single(catalog.Sources.ListProcessing(
            invalid.SourceId), job => job.ProcessorKey == LocalProcessors.SourceText);
        Assert.Equal("failed", failed.State);
        Assert.True(failed.RetryEligible);
        Assert.Equal("invalid_utf8", failed.FailureCode);
        LocalSourceProcessingState unsupportedJob = Assert.Single(catalog.Sources.ListProcessing(
            unsupported.SourceId), job => job.ProcessorKey == LocalProcessors.SourceText);
        Assert.Equal("unsupported", unsupportedJob.State);
        Assert.False(unsupportedJob.RetryEligible);
        Assert.Empty(catalog.Search.Query("asset"));
        Assert.Empty(catalog.Search.Query("malformed"));
        await catalog.Originals.VerifyAsync(unsupported.ItemId);
        await catalog.Originals.VerifyAsync(malformed.ItemId);
    }

    [Fact]
    public async Task ReplayedRequestKeepsOriginalVersionButNewRequestRecordsRevision()
    {
        string path = Path.Combine(manualRoot, "author.md");
        File.WriteAllText(path, "early violet evidence");
        CapturedOriginal first;
        using (LocalCatalog catalog = LocalCatalog.Open(dataRoot))
        {
            first = await new LocalSourceIntake(catalog).ImportFileAsync(path, 100, "author-first");
            Assert.NotNull(catalog.Sources.ReadProvenance(first.SourceId));
        }

        File.WriteAllText(path, "later amber evidence");
        using (LocalCatalog catalog = LocalCatalog.Open(dataRoot))
        {
            LocalSourceIntake intake = new(catalog);
            Assert.Equal(first, await intake.ImportFileAsync(path, 100, "author-first"));
            Assert.Equal(1, catalog.Sources.ReadProvenance(first.SourceId)!.Revision);
            CapturedOriginal later = await intake.ImportFileAsync(path, 100, "author-second");
            Assert.Equal((first.SourceId, first.ItemId, 2), (later.SourceId, later.ItemId, later.Revision));
            Assert.Equal(2, catalog.Sources.ReadProvenance(first.SourceId)!.Revision);
            Assert.Equal(first.Sha256, catalog.Sources.ReadProvenance(first.SourceId, 1)!.OriginalSha256);
            Assert.Equal(later.Sha256, catalog.Sources.ReadProvenance(first.SourceId, 2)!.OriginalSha256);
            Assert.Null(catalog.Sources.ReadProvenance(first.SourceId, 3));
            Assert.Null(catalog.Sources.ReadProvenance(Guid.NewGuid()));
            Assert.Throws<ArgumentException>(() => catalog.Sources.ReadProvenance(Guid.Empty));
            Assert.Throws<ArgumentOutOfRangeException>(() => catalog.Sources.ReadProvenance(first.SourceId, 0));
        }
    }

    private static void AssertCapture(LocalCatalog catalog, Guid sourceId,
        string route, string basis, string scope)
    {
        LocalSourceProvenance source = catalog.Sources.ReadProvenance(sourceId)!;
        Assert.Equal(route, source.CaptureRoute);
        Assert.Equal(basis, source.CaptureAuthorityBasis);
        Assert.Equal(scope, source.CaptureScope);
        Assert.NotNull(source.CaptureRecordedUtc);
        LocalCaptureReceipt receipt = Assert.Single(catalog.Sources.ListCaptureReceipts(sourceId));
        Assert.Equal(route, receipt.Route);
        Assert.Equal(basis, receipt.AuthorityBasis);
        Assert.Equal(scope, receipt.Scope);
        Assert.Equal(source.CaptureRecordedUtc, receipt.RecordedUtc);
    }

    private static void AssertSource(LocalSourceProvenance source)
    {
        Assert.NotEqual(Guid.Empty, source.SourceId);
        Assert.NotEqual(Guid.Empty, source.ItemId);
        Assert.Equal("file", source.SourceKind);
        Assert.True(Path.IsPathFullyQualified(source.Locator));
        Assert.Equal("active", source.Status);
        Assert.True(source.CreatedUtc <= source.ObservedUtc);
        Assert.Equal(1, source.Revision);
        Assert.Matches("^[0-9a-f]{64}$", source.OriginalSha256);
        Assert.True(source.OriginalBytes > 0);
        Assert.Equal(LocalProcessors.SourceText, source.ProcessorKey);
        Assert.Equal("completed", source.ProcessingState);
        Assert.Equal("completed", source.ExtractionOutcome);
        Assert.Null(source.FailureCode);
        Assert.Null(source.ExtractionReasonCode);
        Assert.False(source.RetryEligible);
    }

    private static long Count(string databasePath, string table)
    {
        using SqliteConnection database = new(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        database.Open();
        using SqliteCommand command = database.CreateCommand();
        command.CommandText = table switch
        {
            "original_objects" => "SELECT COUNT(*) FROM original_objects",
            "ingest_requests" => "SELECT COUNT(*) FROM ingest_requests",
            _ => throw new ArgumentException("Unknown test table.", nameof(table))
        };
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
