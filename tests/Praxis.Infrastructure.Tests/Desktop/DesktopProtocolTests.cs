using System.Text.Json;
using Praxis.Application.Desktop;
using Praxis.Domain.Library;
using Praxis.Domain.Originals;
using Praxis.Infrastructure.Intake;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Desktop;

public sealed class DesktopProtocolTests : IDisposable
{
    private readonly string root = Path.Combine(TestPaths.Temp,
        "praxis-session-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LibrarySearchPreviewAndVerificationSharePreservedSources()
    {
        string data = Path.Combine(root, "data");
        string inbox = Path.Combine(root, "inbox");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(inbox);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(data,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        File.WriteAllText(Path.Combine(inbox, "memory.md"), "# project violet memory");
        File.WriteAllBytes(Path.Combine(inbox, "unknown.bin"), [0, 255, 17]);
        using SqliteCatalog catalog = SqliteCatalog.Open(data);
        IntakeHarness watcher = new(catalog, inbox, TimeSpan.Zero);
        await watcher.ReconcileAsync(DateTimeOffset.UtcNow);
        await watcher.ReconcileAsync(DateTimeOffset.UtcNow.AddSeconds(1));
        DesktopProtocol protocol = TestDesktop.Protocol(catalog);

        using JsonDocument library = await Send(protocol,
            """{"requestId":"one","action":"library","limit":10}""");
        Assert.True(library.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(2, library.RootElement.GetProperty("data")
            .GetProperty("items").GetArrayLength());

        LibraryItem text = Assert.Single(catalog.Library.List(), item =>
            item.SourceLocator.EndsWith("memory.md", StringComparison.Ordinal));
        using JsonDocument search = await Send(protocol,
            """{"requestId":"two","action":"search","query":"violet"}""");
        Assert.Equal(text.ItemId.ToString("D"), search.RootElement.GetProperty("data")
            .GetProperty("hits")[0].GetProperty("itemId").GetString());

        using JsonDocument preview = await Send(protocol,
            $$"""{"requestId":"three","action":"preview","itemId":"{{text.ItemId:D}}","revision":1}""");
        Assert.Contains("violet", preview.RootElement.GetProperty("data")
            .GetProperty("content").GetString());

        using JsonDocument verified = await Send(protocol,
            $$"""{"requestId":"four","action":"verify","itemId":"{{text.ItemId:D}}"}""");
        Assert.True(verified.RootElement.GetProperty("data")
            .GetProperty("verified").GetBoolean());

        using JsonDocument unsupported = await Send(protocol,
            $$"""{"requestId":"five","action":"preview","itemId":"{{Assert.Single(catalog.Library.List(), item => item.SourceLocator.EndsWith("unknown.bin", StringComparison.Ordinal)).ItemId:D}}","revision":1}""");
        Assert.Equal("unsupported", unsupported.RootElement.GetProperty("data")
            .GetProperty("outcome").GetString());

        using JsonDocument removed = await Send(protocol,
            $$"""{"requestId":"six","action":"revoke","sourceId":"{{text.SourceId:D}}"}""");
        Assert.True(removed.RootElement.GetProperty("data").GetProperty("revoked").GetBoolean());
        Assert.Empty(catalog.Search.Query("violet"));
        await catalog.Originals.VerifyAsync(text.ItemId);
    }

    [Fact]
    public async Task DesktopCanOrganizeAndFilterCapturedSourcesWithoutChangingOriginals()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        using MemoryStream original = new(System.Text.Encoding.UTF8.GetBytes("trusted violet"));
        CapturedOriginal saved = await catalog.Originals.CaptureFileAsync(
            Path.Combine(root, "source.txt"), original, 100, "source");
        DesktopProtocol protocol = TestDesktop.Protocol(catalog);

        using JsonDocument created = await Send(protocol,
            """{"requestId":"one","action":"collectionCreate","name":"Project"}""");
        Assert.True(created.RootElement.GetProperty("ok").GetBoolean());
        string collectionId = created.RootElement.GetProperty("data")
            .GetProperty("collection").GetProperty("collectionId").GetString()!;
        using JsonDocument added = await Send(protocol,
            $$"""{"requestId":"two","action":"collectionAdd","collectionId":"{{collectionId}}","sourceId":"{{saved.SourceId:D}}"}""");
        Assert.True(added.RootElement.GetProperty("data").GetProperty("changed").GetBoolean());
        using JsonDocument filtered = await Send(protocol,
            $$"""{"requestId":"three","action":"library","collectionId":"{{collectionId}}"}""");
        Assert.Equal(saved.ItemId.ToString("D"), filtered.RootElement
            .GetProperty("data").GetProperty("items")[0].GetProperty("itemId").GetString());

        using JsonDocument memberships = await Send(protocol,
            $$"""{"requestId":"four","action":"sourceCollections","sourceId":"{{saved.SourceId:D}}"}""");
        Assert.Equal(collectionId, memberships.RootElement.GetProperty("data")
            .GetProperty("collectionIds")[0].GetString());

        using JsonDocument renamed = await Send(protocol,
            $$"""{"requestId":"five","action":"collectionRename","collectionId":"{{collectionId}}","name":"Archived Research"}""");
        Assert.True(renamed.RootElement.GetProperty("data").GetProperty("changed").GetBoolean());
        using JsonDocument listed = await Send(protocol,
            """{"requestId":"six","action":"collections"}""");
        Assert.Equal("Archived Research", listed.RootElement.GetProperty("data")
            .GetProperty("collections")[0].GetProperty("name").GetString());

        using JsonDocument removed = await Send(protocol,
            $$"""{"requestId":"seven","action":"collectionRemove","collectionId":"{{collectionId}}","sourceId":"{{saved.SourceId:D}}"}""");
        Assert.True(removed.RootElement.GetProperty("data").GetProperty("changed").GetBoolean());
        using JsonDocument empty = await Send(protocol,
            $$"""{"requestId":"eight","action":"library","collectionId":"{{collectionId}}"}""");
        Assert.Equal(0, empty.RootElement.GetProperty("data")
            .GetProperty("items").GetArrayLength());
        await catalog.Originals.VerifyAsync(saved.ItemId);
    }

    [Fact]
    public async Task SearchAndPreviewHonorSelectedCollectionAndCurrentEvidence()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        using MemoryStream firstText = new(System.Text.Encoding.UTF8.GetBytes("violet approved evidence"));
        using MemoryStream secondText = new(System.Text.Encoding.UTF8.GetBytes("violet unrelated secret"));
        CapturedOriginal allowed = await catalog.Originals.CaptureFileAsync(
            Path.Combine(root, "approved.txt"), firstText, firstText.Length, "approved");
        CapturedOriginal excluded = await catalog.Originals.CaptureFileAsync(
            Path.Combine(root, "excluded.txt"), secondText, secondText.Length, "excluded");
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());

        Guid collectionId = catalog.Collections.Create("Selected").CollectionId;
        Assert.True(catalog.Collections.AddSource(collectionId, allowed.SourceId));
        DesktopProtocol protocol = TestDesktop.Protocol(catalog);
        using JsonDocument search = await Send(protocol,
            $$"""{"requestId":"find","action":"search","query":"violet","collectionId":"{{collectionId:D}}"}""");
        Assert.True(search.RootElement.GetProperty("ok").GetBoolean());
        JsonElement hit = Assert.Single(search.RootElement.GetProperty("data").GetProperty("hits").EnumerateArray());
        Assert.Equal(allowed.SourceId.ToString("D"), hit.GetProperty("sourceId").GetString());
        Assert.DoesNotContain("secret", hit.GetProperty("snippet").GetString()!);

        using JsonDocument preview = await Send(protocol,
            $$"""{"requestId":"peek","action":"preview","itemId":"{{allowed.ItemId:D}}","revision":1,"sourceId":"{{allowed.SourceId:D}}","originalSha256":"{{allowed.Sha256}}","collectionId":"{{collectionId:D}}"}""");
        Assert.Equal("completed", preview.RootElement.GetProperty("data").GetProperty("outcome").GetString());
        Assert.Contains("approved", preview.RootElement.GetProperty("data").GetProperty("content").GetString()!);

        using JsonDocument mismatched = await Send(protocol,
            $$"""{"requestId":"wrong","action":"preview","itemId":"{{allowed.ItemId:D}}","revision":1,"sourceId":"{{excluded.SourceId:D}}","originalSha256":"{{allowed.Sha256}}","collectionId":"{{collectionId:D}}"}""");
        Assert.Equal("unavailable", mismatched.RootElement.GetProperty("data").GetProperty("outcome").GetString());

        Assert.True(catalog.Collections.RemoveSource(collectionId, allowed.SourceId));
        using JsonDocument removed = await Send(protocol,
            $$"""{"requestId":"removed","action":"preview","itemId":"{{allowed.ItemId:D}}","revision":1,"sourceId":"{{allowed.SourceId:D}}","originalSha256":"{{allowed.Sha256}}","collectionId":"{{collectionId:D}}"}""");
        Assert.Equal("unavailable", removed.RootElement.GetProperty("data").GetProperty("outcome").GetString());

        using JsonDocument deniedSearch = await Send(protocol,
            $$"""{"requestId":"denied","action":"search","query":"violet","collectionId":"{{collectionId:D}}"}""");
        Assert.Empty(deniedSearch.RootElement.GetProperty("data").GetProperty("hits").EnumerateArray());

        Assert.True(catalog.Collections.AddSource(collectionId, allowed.SourceId));
        using MemoryStream revisedText = new(System.Text.Encoding.UTF8.GetBytes("amber updated evidence"));
        CapturedOriginal revised = await catalog.Originals.CaptureFileAsync(
            Path.Combine(root, "approved.txt"), revisedText, revisedText.Length, "updated");
        Assert.Equal(allowed.ItemId, revised.ItemId);
        Assert.Equal(2, revised.Revision);

        using JsonDocument stale = await Send(protocol,
            $$"""{"requestId":"stale","action":"preview","itemId":"{{allowed.ItemId:D}}","revision":1,"sourceId":"{{allowed.SourceId:D}}","originalSha256":"{{allowed.Sha256}}","collectionId":"{{collectionId:D}}"}""");
        Assert.Equal("unavailable", stale.RootElement.GetProperty("data").GetProperty("outcome").GetString());

        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        using JsonDocument current = await Send(protocol,
            $$"""{"requestId":"latest","action":"preview","itemId":"{{allowed.ItemId:D}}","revision":2,"sourceId":"{{allowed.SourceId:D}}","originalSha256":"{{revised.Sha256}}","collectionId":"{{collectionId:D}}"}""");
        Assert.Contains("updated", current.RootElement.GetProperty("data").GetProperty("content").GetString()!);

        Assert.True(catalog.Sources.Revoke(allowed.SourceId));
        using JsonDocument revoked = await Send(protocol,
            $$"""{"requestId":"revoked","action":"preview","itemId":"{{allowed.ItemId:D}}","revision":2,"sourceId":"{{allowed.SourceId:D}}","originalSha256":"{{revised.Sha256}}","collectionId":"{{collectionId:D}}"}""");
        Assert.Equal("unavailable", revoked.RootElement.GetProperty("data").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task PrivateQuickCaptureProtocolPreservesAndIndexesTheSubmittedNote()
    {
        string data = Path.Combine(root, "data");
        string inbox = Path.Combine(root, "inbox");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(inbox);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(data,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(inbox,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using SqliteCatalog catalog = SqliteCatalog.Open(data);
        DesktopProtocol service = TestDesktop.Protocol(catalog, new QuickNoteCapture(catalog.Originals, catalog.DataDirectory, inbox));
        Guid captureId = Guid.NewGuid();
        string payload =
            $$"""{"requestId":"save","action":"noteCapture","captureId":"{{captureId:D}}","title":"Inbox Thought","text":"A violet first thought."}""";
        using JsonDocument saved = await Send(service, payload);
        Assert.True(saved.RootElement.GetProperty("ok").GetBoolean());
        JsonElement source = saved.RootElement.GetProperty("data")
            .GetProperty("capture").GetProperty("original");
        string? itemId = source.GetProperty("itemId").GetString();
        string? originalDigest = source.GetProperty("sha256").GetString();
        string? filename = saved.RootElement.GetProperty("data")
            .GetProperty("capture").GetProperty("sourcePath").GetString();
        Assert.Equal("# Inbox Thought\n\nA violet first thought.", File.ReadAllText(filename!));
        Assert.Equal(itemId, Assert.Single(catalog.Library.List()).ItemId.ToString("D"));

        using JsonDocument retried = await Send(service, payload);
        Assert.Equal(itemId, retried.RootElement.GetProperty("data")
            .GetProperty("capture").GetProperty("original")
            .GetProperty("itemId").GetString());
        Assert.Single(catalog.Library.List());
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        Assert.Equal(originalDigest, Assert.Single(catalog.Search.Query("violet")).OriginalSha256);

        using JsonDocument mismatch = await Send(service,
            $$"""{"requestId":"mismatch","action":"noteCapture","captureId":"{{captureId:D}}","title":"Inbox Thought","text":"Another body"}""");
        Assert.False(mismatch.RootElement.GetProperty("ok").GetBoolean());
        Assert.Single(Directory.EnumerateFiles(inbox, "*.md", SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task QuickCaptureIsDeniedWithoutApprovedIntakeRoot()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        DesktopProtocol protocol = TestDesktop.Protocol(catalog);
        using JsonDocument response = await Send(protocol,
            $$"""{"requestId":"x","action":"noteCapture","captureId":"{{Guid.NewGuid():D}}","title":"Private","text":"Do not save."}""");
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(catalog.Library.List());
    }

    [Fact]
    public async Task ReviewTopicsThroughTrustedSessionWithoutChangingCanonicalSources()
    {
        string data = Path.Combine(root, "catalog");
        Directory.CreateDirectory(data);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(data,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using SqliteCatalog catalog = SqliteCatalog.Open(data);
        using MemoryStream stream = new(System.Text.Encoding.UTF8.GetBytes(
            "# Evidence Topic\nOriginal retained"));
        CapturedOriginal captured = await catalog.Originals.CaptureFileAsync(
            Path.Combine(root, "topic.md"), stream, 4_096, "topic");
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        await ProcessingDriver.SuggestHeadingTopicsAsync(catalog);
        DesktopProtocol protocol = TestDesktop.Protocol(catalog);

        using JsonDocument listing = await Send(protocol,
            $$"""{"requestId":"topics","action":"topics","sourceId":"{{captured.SourceId:D}}"}""");
        Assert.True(listing.RootElement.GetProperty("ok").GetBoolean());
        JsonElement proposal = listing.RootElement.GetProperty("data").GetProperty("proposals")[0];
        string id = proposal.GetProperty("proposalId").GetString()!;
        Assert.Equal("Evidence Topic", proposal.GetProperty("value").GetString());
        Assert.Equal(captured.Sha256, proposal.GetProperty("sourceSha256").GetString());
        Assert.Equal("semantic-extract-v2", proposal.GetProperty("producer").GetString());
        Assert.False(proposal.GetProperty("confirmed").GetBoolean());
        Assert.Equal("generated", listing.RootElement.GetProperty("data")
            .GetProperty("semanticOutcome").GetString());

        using JsonDocument approved = await Send(protocol,
            $$"""{"requestId":"approved","action":"topicReview","proposalId":"{{id}}","decision":"accepted"}""");
        Assert.True(approved.RootElement.GetProperty("data").GetProperty("reviewed").GetBoolean());
        using JsonDocument correction = await Send(protocol,
            $$"""{"requestId":"correction","action":"topicReview","proposalId":"{{id}}","decision":"corrected","correction":"Verified owner topic"}""");
        Assert.True(correction.RootElement.GetProperty("data").GetProperty("reviewed").GetBoolean());
        Assert.Equal("Verified owner topic",
            Assert.Single(catalog.Proposals.List(captured.SourceId)).Correction);

        using JsonDocument invalid = await Send(protocol,
            $$"""{"requestId":"invalid","action":"topicReview","proposalId":"{{id}}","decision":"run"}""");
        Assert.False(invalid.RootElement.GetProperty("ok").GetBoolean());

        Assert.True(catalog.Sources.Revoke(captured.SourceId));
        using JsonDocument withheld = await Send(protocol,
            $$"""{"requestId":"withheld","action":"topics","sourceId":"{{captured.SourceId:D}}"}""");
        Assert.Equal(0, withheld.RootElement.GetProperty("data")
            .GetProperty("proposals").GetArrayLength());
        using JsonDocument stale = await Send(protocol,
            $$"""{"requestId":"stale","action":"topicReview","proposalId":"{{id}}","decision":"rejected"}""");
        Assert.False(stale.RootElement.GetProperty("data").GetProperty("reviewed").GetBoolean());
        await catalog.Originals.VerifyAsync(captured.ItemId);
    }

    [Fact]
    public async Task PrivateDesktopExportsVerifiedBinaryWithoutExposingCatalogPaths()
    {
        string data = Path.Combine(root, "data");
        string exports = Path.Combine(root, "exports");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(exports);
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(data, mode);
            File.SetUnixFileMode(exports, mode);
        }

        using SqliteCatalog catalog = SqliteCatalog.Open(data);
        using MemoryStream input = new([0, 255, 17, 42, 99]);
        CapturedOriginal captured = await catalog.Originals.CaptureFileAsync(
            Path.Combine(root, "missing", "binary.bin"), input, 100, "original");
        DesktopProtocol protocol = TestDesktop.Protocol(catalog);
        string destination = Path.Combine(exports, "recovered.bin");
        string request = JsonSerializer.Serialize(new
        {
            requestId = "export",
            action = "exportOriginal",
            itemId = captured.ItemId,
            revision = captured.Revision,
            destinationPath = destination
        });

        using JsonDocument response = await Send(protocol, request);
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        JsonElement receipt = response.RootElement.GetProperty("data").GetProperty("exported");
        Assert.Equal(captured.Sha256, receipt.GetProperty("sha256").GetString());
        Assert.Equal(captured.Length, receipt.GetProperty("length").GetInt64());
        Assert.Equal(new byte[] { 0, 255, 17, 42, 99 }, File.ReadAllBytes(destination));

        using JsonDocument repeated = await Send(protocol, request);
        Assert.False(repeated.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(new byte[] { 0, 255, 17, 42, 99 }, File.ReadAllBytes(destination));

        Assert.True(catalog.Sources.Revoke(captured.SourceId));
        string revokedRequest = JsonSerializer.Serialize(new
        {
            requestId = "denied",
            action = "exportOriginal",
            itemId = captured.ItemId,
            revision = 1,
            destinationPath = Path.Combine(exports, "blocked.bin")
        });
        using JsonDocument revoked = await Send(protocol, revokedRequest);
        Assert.False(revoked.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(File.Exists(Path.Combine(exports, "blocked.bin")));
    }

    [Fact]
    public async Task TrustedDesktopCanInspectPauseResumeAndRetrySupervisedIntake()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        IntakeHarness intake = new(catalog,
            Path.Combine(root, "not-started"), TimeSpan.Zero);
        DesktopProtocol service = TestDesktop.Protocol(catalog, monitor: intake.Monitor, processing: intake.Processing);

        using JsonDocument initial = await Send(service,
            """{"requestId":"initial","action":"intakeStatus"}""");
        Assert.Equal("starting", initial.RootElement.GetProperty("data")
            .GetProperty("status").GetProperty("state").GetString());
        JsonElement initialBacklog = initial.RootElement.GetProperty("data").GetProperty("backlog");
        Assert.Equal(0, initialBacklog.GetProperty("queued").GetInt64());
        Assert.Equal(0, initialBacklog.GetProperty("running").GetInt64());
        Assert.Equal(0, initialBacklog.GetProperty("failed").GetInt64());
        Assert.Equal(0, initial.RootElement.GetProperty("data").GetProperty("incidents").GetArrayLength());

        using MemoryStream pending = new([7, 8]);
        CapturedOriginal captured = await catalog.Originals.CaptureFileAsync(
            Path.Combine(Path.GetDirectoryName(root)!, "external-source.txt"), pending, 100, "queued-health");
        using JsonDocument queuedStatus = await Send(service,
            """{"requestId":"queue","action":"intakeStatus"}""");
        Assert.Equal(1, queuedStatus.RootElement.GetProperty("data")
            .GetProperty("backlog").GetProperty("queued").GetInt64());
        Assert.True(catalog.Sources.Revoke(captured.SourceId));
        using JsonDocument revokedStatus = await Send(service,
            """{"requestId":"revoked","action":"intakeStatus"}""");
        Assert.Equal(0, revokedStatus.RootElement.GetProperty("data")
            .GetProperty("backlog").GetProperty("queued").GetInt64());

        using JsonDocument paused = await Send(service,
            """{"requestId":"pause","action":"intakePause","paused":true}""");
        Assert.True(paused.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(paused.RootElement.GetProperty("data")
            .GetProperty("status").GetProperty("paused").GetBoolean());

        using JsonDocument retried = await Send(service,
            """{"requestId":"retry","action":"intakeRescan"}""");
        Assert.Equal("paused", retried.RootElement.GetProperty("data")
            .GetProperty("status").GetProperty("state").GetString());

        using JsonDocument resumed = await Send(service,
            """{"requestId":"resume","action":"intakePause","paused":false}""");
        Assert.False(resumed.RootElement.GetProperty("data")
            .GetProperty("status").GetProperty("paused").GetBoolean());

        using JsonDocument malformed = await Send(service,
            """{"requestId":"malformed","action":"intakePause","paused":"true"}""");
        Assert.False(malformed.RootElement.GetProperty("ok").GetBoolean());

        DesktopProtocol unconfigured = TestDesktop.Protocol(catalog);
        using JsonDocument denied = await Send(unconfigured,
            """{"requestId":"denied","action":"intakeStatus"}""");
        Assert.False(denied.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task DesktopShowsSourceMovementEvidenceOnlyWhileSourceIsActive()
    {
        string data = Path.Combine(root, "data");
        string inbox = Path.Combine(root, "inbox");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(inbox);
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(data, mode);
            File.SetUnixFileMode(inbox, mode);
        }

        string previous = Path.Combine(inbox, "original.md");
        string current = Path.Combine(inbox, "renamed.md");
        File.WriteAllText(previous, "# Rename evidence");
        using SqliteCatalog catalog = SqliteCatalog.Open(data);
        IntakeHarness monitor = new(catalog, inbox, TimeSpan.Zero);
        DateTimeOffset time = DateTimeOffset.UtcNow;
        await monitor.ReconcileAsync(time);
        await monitor.ReconcileAsync(time.AddSeconds(1));
        LibraryItem source = Assert.Single(catalog.Library.List());

        File.Move(previous, current);
        monitor.Monitor.QueueRename(previous, current);
        await monitor.ReconcileAsync(time.AddSeconds(2));

        DesktopProtocol protocol = TestDesktop.Protocol(catalog, monitor: monitor.Monitor, processing: monitor.Processing);
        using JsonDocument listed = await Send(protocol,
            $$"""{"requestId":"moves","action":"sourceMoves","sourceId":"{{source.SourceId:D}}"}""");
        Assert.True(listed.RootElement.GetProperty("ok").GetBoolean());
        JsonElement move = listed.RootElement.GetProperty("data").GetProperty("moves")[0];
        Assert.Equal(previous, move.GetProperty("previousPath").GetString());
        Assert.Equal(current, move.GetProperty("currentPath").GetString());
        Assert.Equal(source.Sha256, move.GetProperty("sha256").GetString());

        Assert.True(catalog.Sources.Revoke(source.SourceId));
        using JsonDocument revoked = await Send(protocol,
            $$"""{"requestId":"revoked","action":"sourceMoves","sourceId":"{{source.SourceId:D}}"}""");
        Assert.Equal(0, revoked.RootElement.GetProperty("data")
            .GetProperty("moves").GetArrayLength());

        using JsonDocument invalid = await Send(protocol,
            """{"requestId":"invalid","action":"sourceMoves","sourceId":"bad"}""");
        Assert.False(invalid.RootElement.GetProperty("ok").GetBoolean());
        await catalog.Originals.VerifyAsync(source.ItemId);
    }

    [Fact]
    public async Task DesktopExportUsesCurrentPreservedOriginalAndDeniesConflictsOrRevocation()
    {
        string data = Path.Combine(root, "catalog");
        string exported = Path.Combine(root, "exported");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(exported);
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode mode = UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(root, mode);
            File.SetUnixFileMode(data, mode);
            File.SetUnixFileMode(exported, mode);
        }

        using SqliteCatalog catalog = SqliteCatalog.Open(data);
        byte[] bytes = [0, 255, 17, 128, 42];
        using MemoryStream stream = new(bytes);
        CapturedOriginal captured = await catalog.Originals.CaptureFileAsync(
            Path.Combine(root, "no-longer-present.bin"), stream, 1024, "binary");
        DesktopProtocol protocol = TestDesktop.Protocol(catalog);
        string destination = Path.Combine(exported, "saved.bin");

        string Request(string requestId, int revision, string output) => JsonSerializer.Serialize(new
        {
            requestId,
            action = "exportOriginal",
            itemId = captured.ItemId.ToString("D"),
            revision,
            destinationPath = output
        });

        using JsonDocument success = await Send(protocol, Request("success", 1, destination));
        Assert.True(success.RootElement.GetProperty("ok").GetBoolean());
        JsonElement receipt = success.RootElement.GetProperty("data").GetProperty("exported");
        Assert.Equal(captured.ItemId.ToString("D"), receipt.GetProperty("itemId").GetString());
        Assert.Equal(captured.Sha256, receipt.GetProperty("sha256").GetString());
        Assert.Equal(bytes.LongLength, receipt.GetProperty("length").GetInt64());
        Assert.Equal(bytes, File.ReadAllBytes(destination));

        using JsonDocument conflict = await Send(protocol, Request("conflict", 1, destination));
        Assert.False(conflict.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("already exists", conflict.RootElement.GetProperty("error").GetString());
        Assert.Equal(bytes, File.ReadAllBytes(destination));

        using JsonDocument wrongRevision = await Send(protocol,
            Request("wrong", 2, Path.Combine(exported, "wrong.bin")));
        Assert.False(wrongRevision.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(File.Exists(Path.Combine(exported, "wrong.bin")));

        Assert.True(catalog.Sources.Revoke(captured.SourceId));
        using JsonDocument revoked = await Send(protocol,
            Request("revoked", 1, Path.Combine(exported, "revoked.bin")));
        Assert.False(revoked.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(File.Exists(Path.Combine(exported, "revoked.bin")));
        Assert.Empty(Directory.EnumerateFiles(exported, ".praxis-export-*.part"));
        await catalog.Originals.VerifyAsync(captured.ItemId);
    }

    [Theory]
    [InlineData("""{"requestId":"x","action":"execute","command":"rm"}""")]
    [InlineData("""{"requestId":"x","action":"library","limit":10000}""")]
    [InlineData("""{"requestId":"x","action":"preview","itemId":"invalid"}""")]
    [InlineData("""{"requestId":"x","action":"search","query":"   "}""")]
    [InlineData("""{"action":"library"}""")]
    [InlineData("""{"requestId":"x","action":"collectionCreate","name":"  "}""")]
    [InlineData("""{"requestId":"x","action":"topics","sourceId":"bad"}""")]
    [InlineData("""{"requestId":"x","action":"topicReview","proposalId":"bad","decision":"accepted"}""")]
    [InlineData("""{"requestId":"x","action":"exportOriginal","itemId":"bad","revision":1,"destinationPath":"/tmp/file"}""")]
    [InlineData("""{"requestId":"x","action":"collectionAdd","collectionId":"invalid","sourceId":"invalid"}""")]
    public async Task RejectsUnknownOrMalformedRequests(string payload)
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        DesktopProtocol protocol = TestDesktop.Protocol(catalog);
        using JsonDocument response = await Send(protocol, payload);
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
    }

    [Theory]
    [InlineData("budgetSummary")]
    [InlineData("budgetScenario")]
    public async Task DesktopProtocolDoesNotExposeBudgetCommands(string action)
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using SqliteCatalog catalog = SqliteCatalog.Open(root);
        DesktopProtocol protocol = TestDesktop.Protocol(catalog);
        using JsonDocument result = await Send(protocol, $$"""
                                                           {"requestId":"retired","action":"{{action}}"}
                                                           """);
        Assert.False(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("The desktop action is not supported.",
            result.RootElement.GetProperty("error").GetString());
        Assert.False(result.RootElement.TryGetProperty("data", out _));
    }

    private static async Task<JsonDocument> Send(DesktopProtocol protocol, string payload) =>
        JsonDocument.Parse(await protocol.HandleAsync(payload));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
