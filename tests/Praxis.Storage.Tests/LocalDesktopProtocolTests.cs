using System.Text.Json;
using Praxis.Storage;
using Xunit;

namespace Praxis.Storage.Tests;

public sealed class LocalDesktopProtocolTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "praxis-session-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LibrarySearchPreviewAndVerificationSharePreservedSources()
    {
        var data = Path.Combine(root, "data");
        var inbox = Path.Combine(root, "inbox");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(inbox);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(data,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        File.WriteAllText(Path.Combine(inbox, "memory.md"), "# project violet memory");
        File.WriteAllBytes(Path.Combine(inbox, "unknown.bin"), [0, 255, 17]);
        using var catalog = LocalCatalog.Open(data);
        var watcher = new LocalWatchedIntake(catalog, inbox, TimeSpan.Zero);
        await watcher.ReconcileAsync(DateTimeOffset.UtcNow);
        await watcher.ReconcileAsync(DateTimeOffset.UtcNow.AddSeconds(1));
        var protocol = new LocalDesktopProtocol(catalog);

        using var library = await Send(protocol,
            """{"requestId":"one","action":"library","limit":10}""");
        Assert.True(library.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(2, library.RootElement.GetProperty("data")
            .GetProperty("items").GetArrayLength());

        var text = Assert.Single(catalog.ListLibrary(), item =>
            item.SourceLocator.EndsWith("memory.md", StringComparison.Ordinal));
        using var search = await Send(protocol,
            """{"requestId":"two","action":"search","query":"violet"}""");
        Assert.Equal(text.ItemId.ToString("D"), search.RootElement.GetProperty("data")
            .GetProperty("hits")[0].GetProperty("itemId").GetString());

        using var preview = await Send(protocol,
            $$"""{"requestId":"three","action":"preview","itemId":"{{text.ItemId:D}}","revision":1}""");
        Assert.Contains("violet", preview.RootElement.GetProperty("data")
            .GetProperty("content").GetString());

        using var verified = await Send(protocol,
            $$"""{"requestId":"four","action":"verify","itemId":"{{text.ItemId:D}}"}""");
        Assert.True(verified.RootElement.GetProperty("data")
            .GetProperty("verified").GetBoolean());

        using var unsupported = await Send(protocol,
            $$"""{"requestId":"five","action":"preview","itemId":"{{Assert.Single(catalog.ListLibrary(), item => item.SourceLocator.EndsWith("unknown.bin", StringComparison.Ordinal)).ItemId:D}}","revision":1}""");
        Assert.Equal("unsupported", unsupported.RootElement.GetProperty("data")
            .GetProperty("outcome").GetString());

        using var removed = await Send(protocol,
            $$"""{"requestId":"six","action":"revoke","sourceId":"{{text.SourceId:D}}"}""");
        Assert.True(removed.RootElement.GetProperty("data").GetProperty("revoked").GetBoolean());
        Assert.Empty(catalog.SearchText("violet"));
        await catalog.VerifyOriginalAsync(text.ItemId);
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

        using var catalog = LocalCatalog.Open(root);
        using var original = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("trusted violet"));
        var saved = await catalog.CaptureFileAsync(
            Path.Combine(root, "source.txt"), original, 100, "source");
        var protocol = new LocalDesktopProtocol(catalog);

        using var created = await Send(protocol,
            """{"requestId":"one","action":"collectionCreate","name":"Project"}""");
        Assert.True(created.RootElement.GetProperty("ok").GetBoolean());
        var collectionId = created.RootElement.GetProperty("data")
            .GetProperty("collection").GetProperty("collectionId").GetString()!;
        using var added = await Send(protocol,
            $$"""{"requestId":"two","action":"collectionAdd","collectionId":"{{collectionId}}","sourceId":"{{saved.SourceId:D}}"}""");
        Assert.True(added.RootElement.GetProperty("data").GetProperty("changed").GetBoolean());
        using var filtered = await Send(protocol,
            $$"""{"requestId":"three","action":"library","collectionId":"{{collectionId}}"}""");
        Assert.Equal(saved.ItemId.ToString("D"), filtered.RootElement
            .GetProperty("data").GetProperty("items")[0].GetProperty("itemId").GetString());

        using var memberships = await Send(protocol,
            $$"""{"requestId":"four","action":"sourceCollections","sourceId":"{{saved.SourceId:D}}"}""");
        Assert.Equal(collectionId, memberships.RootElement.GetProperty("data")
            .GetProperty("collectionIds")[0].GetString());

        using var renamed = await Send(protocol,
            $$"""{"requestId":"five","action":"collectionRename","collectionId":"{{collectionId}}","name":"Archived Research"}""");
        Assert.True(renamed.RootElement.GetProperty("data").GetProperty("changed").GetBoolean());
        using var listed = await Send(protocol,
            """{"requestId":"six","action":"collections"}""");
        Assert.Equal("Archived Research", listed.RootElement.GetProperty("data")
            .GetProperty("collections")[0].GetProperty("name").GetString());

        using var removed = await Send(protocol,
            $$"""{"requestId":"seven","action":"collectionRemove","collectionId":"{{collectionId}}","sourceId":"{{saved.SourceId:D}}"}""");
        Assert.True(removed.RootElement.GetProperty("data").GetProperty("changed").GetBoolean());
        using var empty = await Send(protocol,
            $$"""{"requestId":"eight","action":"library","collectionId":"{{collectionId}}"}""");
        Assert.Equal(0, empty.RootElement.GetProperty("data")
            .GetProperty("items").GetArrayLength());
        await catalog.VerifyOriginalAsync(saved.ItemId);
    }

    [Fact]
    public async Task PrivateQuickCaptureProtocolPreservesAndIndexesTheSubmittedNote()
    {
        var data = Path.Combine(root, "data");
        var inbox = Path.Combine(root, "inbox");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(inbox);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(data,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.SetUnixFileMode(inbox,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using var catalog = LocalCatalog.Open(data);
        var service = new LocalDesktopProtocol(catalog, new LocalQuickCapture(catalog, inbox));
        var captureId = Guid.NewGuid();
        var payload =
            $$"""{"requestId":"save","action":"noteCapture","captureId":"{{captureId:D}}","title":"Inbox Thought","text":"A violet first thought."}""";
        using var saved = await Send(service, payload);
        Assert.True(saved.RootElement.GetProperty("ok").GetBoolean());
        var source = saved.RootElement.GetProperty("data")
            .GetProperty("capture").GetProperty("original");
        var itemId = source.GetProperty("itemId").GetString();
        var originalDigest = source.GetProperty("sha256").GetString();
        var filename = saved.RootElement.GetProperty("data")
            .GetProperty("capture").GetProperty("sourcePath").GetString();
        Assert.Equal("# Inbox Thought\n\nA violet first thought.", File.ReadAllText(filename!));
        Assert.Equal(itemId, Assert.Single(catalog.ListLibrary()).ItemId.ToString("D"));

        using var retried = await Send(service, payload);
        Assert.Equal(itemId, retried.RootElement.GetProperty("data")
            .GetProperty("capture").GetProperty("original")
            .GetProperty("itemId").GetString());
        Assert.Single(catalog.ListLibrary());
        Assert.True(await new LocalTextProcessor(catalog).ProcessNextAsync());
        Assert.Equal(originalDigest, Assert.Single(catalog.SearchText("violet")).OriginalSha256);

        using var mismatch = await Send(service,
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

        using var catalog = LocalCatalog.Open(root);
        var protocol = new LocalDesktopProtocol(catalog);
        using var response = await Send(protocol,
            $$"""{"requestId":"x","action":"noteCapture","captureId":"{{Guid.NewGuid():D}}","title":"Private","text":"Do not save."}""");
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Empty(catalog.ListLibrary());
    }

    [Fact]
    public async Task ReviewTopicsThroughTrustedSessionWithoutChangingCanonicalSources()
    {
        var data = Path.Combine(root, "catalog");
        Directory.CreateDirectory(data);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(data,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using var catalog = LocalCatalog.Open(data);
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "# Evidence Topic\nOriginal retained"));
        var captured = await catalog.CaptureFileAsync(
            Path.Combine(root, "topic.md"), stream, 4_096, "topic");
        Assert.True(await new LocalTextProcessor(catalog).ProcessNextAsync());
        Assert.True(new LocalTopicProposer(catalog).ProcessNext());
        var protocol = new LocalDesktopProtocol(catalog);

        using var listing = await Send(protocol,
            $"""{"requestId":"topics","action":"topics","sourceId":"{{captured.SourceId:D}}"}""");
        Assert.True(listing.RootElement.GetProperty("ok").GetBoolean());
        var proposal = listing.RootElement.GetProperty("data").GetProperty("proposals")[0];
        var id = proposal.GetProperty("proposalId").GetString()!;
        Assert.Equal("Evidence Topic", proposal.GetProperty("value").GetString());
        Assert.Equal(captured.Sha256, proposal.GetProperty("sourceSha256").GetString());
        Assert.Equal("markdown-heading-v1", proposal.GetProperty("producer").GetString());
        Assert.Equal("generated", listing.RootElement.GetProperty("data")
            .GetProperty("outcome").GetString());

        using var approved = await Send(protocol,
            $"""{"requestId":"approved","action":"topicReview","proposalId":"{{id}}","decision":"accepted"}""");
        Assert.True(approved.RootElement.GetProperty("data").GetProperty("reviewed").GetBoolean());
        using var correction = await Send(protocol,
            $"""{"requestId":"correction","action":"topicReview","proposalId":"{{id}}","decision":"corrected","correction":"Verified owner topic"}""");
        Assert.True(correction.RootElement.GetProperty("data").GetProperty("reviewed").GetBoolean());
        Assert.Equal("Verified owner topic",
            Assert.Single(catalog.ListTopicProposals(captured.SourceId)).Correction);

        using var invalid = await Send(protocol,
            $"""{"requestId":"invalid","action":"topicReview","proposalId":"{{id}}","decision":"run"}""");
        Assert.False(invalid.RootElement.GetProperty("ok").GetBoolean());

        Assert.True(catalog.RevokeSource(captured.SourceId));
        using var withheld = await Send(protocol,
            $"""{"requestId":"withheld","action":"topics","sourceId":"{{captured.SourceId:D}}"}""");
        Assert.Equal(0, withheld.RootElement.GetProperty("data")
            .GetProperty("proposals").GetArrayLength());
        using var stale = await Send(protocol,
            $"""{"requestId":"stale","action":"topicReview","proposalId":"{{id}}","decision":"rejected"}""");
        Assert.False(stale.RootElement.GetProperty("data").GetProperty("reviewed").GetBoolean());
        await catalog.VerifyOriginalAsync(captured.ItemId);
    }

    [Fact]
    public async Task PrivateDesktopExportsVerifiedBinaryWithoutExposingCatalogPaths()
    {
        var data = Path.Combine(root, "data");
        var exports = Path.Combine(root, "exports");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(exports);
        if (!OperatingSystem.IsWindows())
        {
            var mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(data, mode);
            File.SetUnixFileMode(exports, mode);
        }

        using var catalog = LocalCatalog.Open(data);
        using var input = new MemoryStream(new byte[] { 0, 255, 17, 42, 99 });
        var captured = await catalog.CaptureFileAsync(
            Path.Combine(root, "missing", "binary.bin"), input, 100, "original");
        var protocol = new LocalDesktopProtocol(catalog);
        var destination = Path.Combine(exports, "recovered.bin");
        var request = JsonSerializer.Serialize(new
        {
            requestId = "export",
            action = "exportOriginal",
            itemId = captured.ItemId,
            revision = captured.Revision,
            destinationPath = destination
        });

        using var response = await Send(protocol, request);
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        var receipt = response.RootElement.GetProperty("data").GetProperty("exported");
        Assert.Equal(captured.Sha256, receipt.GetProperty("sha256").GetString());
        Assert.Equal(captured.Length, receipt.GetProperty("length").GetInt64());
        Assert.Equal(new byte[] { 0, 255, 17, 42, 99 }, File.ReadAllBytes(destination));

        using var repeated = await Send(protocol, request);
        Assert.False(repeated.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(new byte[] { 0, 255, 17, 42, 99 }, File.ReadAllBytes(destination));

        Assert.True(catalog.RevokeSource(captured.SourceId));
        using var revoked = await Send(protocol,
            JsonSerializer.Serialize(new {
                requestId = "denied", action = "exportOriginal",
                itemId = captured.ItemId, revision = 1,
                destinationPath = Path.Combine(exports, "blocked.bin")
            }));
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

        using var catalog = LocalCatalog.Open(root);
        var intake = new LocalWatchedIntake(catalog,
            Path.Combine(root, "not-started"), TimeSpan.Zero);
        var service = new LocalDesktopProtocol(catalog, monitor: intake);

        using var initial = await Send(service,
            """{"requestId":"initial","action":"intakeStatus"}""");
        Assert.Equal("starting", initial.RootElement.GetProperty("data")
            .GetProperty("status").GetProperty("state").GetString());

        using var paused = await Send(service,
            """{"requestId":"pause","action":"intakePause","paused":true}""");
        Assert.True(paused.RootElement.GetProperty("ok").GetBoolean());
        Assert.True(paused.RootElement.GetProperty("data")
            .GetProperty("status").GetProperty("paused").GetBoolean());

        using var retried = await Send(service,
            """{"requestId":"retry","action":"intakeRescan"}""");
        Assert.Equal("paused", retried.RootElement.GetProperty("data")
            .GetProperty("status").GetProperty("state").GetString());

        using var resumed = await Send(service,
            """{"requestId":"resume","action":"intakePause","paused":false}""");
        Assert.False(resumed.RootElement.GetProperty("data")
            .GetProperty("status").GetProperty("paused").GetBoolean());

        using var malformed = await Send(service,
            """{"requestId":"malformed","action":"intakePause","paused":"true"}""");
        Assert.False(malformed.RootElement.GetProperty("ok").GetBoolean());

        var unconfigured = new LocalDesktopProtocol(catalog);
        using var denied = await Send(unconfigured,
            """{"requestId":"denied","action":"intakeStatus"}""");
        Assert.False(denied.RootElement.GetProperty("ok").GetBoolean());
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

        using var catalog = LocalCatalog.Open(root);
        var protocol = new LocalDesktopProtocol(catalog);
        using var response = await Send(protocol, payload);
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
    }

    private static async Task<JsonDocument> Send(LocalDesktopProtocol protocol, string payload) =>
        JsonDocument.Parse(await protocol.HandleAsync(payload));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
