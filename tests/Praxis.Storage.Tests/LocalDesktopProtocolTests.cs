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
            $"""{"requestId":"two","action":"collectionAdd","collectionId":"{{collectionId}}","sourceId":"{{saved.SourceId:D}}"}""");
        Assert.True(added.RootElement.GetProperty("data").GetProperty("changed").GetBoolean());
        using var filtered = await Send(protocol,
            $"""{"requestId":"three","action":"library","collectionId":"{{collectionId}}"}""");
        Assert.Equal(saved.ItemId.ToString("D"), filtered.RootElement
            .GetProperty("data").GetProperty("items")[0].GetProperty("itemId").GetString());

        using var memberships = await Send(protocol,
            $"""{"requestId":"four","action":"sourceCollections","sourceId":"{{saved.SourceId:D}}"}""");
        Assert.Equal(collectionId, memberships.RootElement.GetProperty("data")
            .GetProperty("collectionIds")[0].GetString());

        using var renamed = await Send(protocol,
            $"""{"requestId":"five","action":"collectionRename","collectionId":"{{collectionId}}","name":"Archived Research"}""");
        Assert.True(renamed.RootElement.GetProperty("data").GetProperty("changed").GetBoolean());
        using var listed = await Send(protocol,
            """{"requestId":"six","action":"collections"}""");
        Assert.Equal("Archived Research", listed.RootElement.GetProperty("data")
            .GetProperty("collections")[0].GetProperty("name").GetString());

        using var removed = await Send(protocol,
            $"""{"requestId":"seven","action":"collectionRemove","collectionId":"{{collectionId}}","sourceId":"{{saved.SourceId:D}}"}""");
        Assert.True(removed.RootElement.GetProperty("data").GetProperty("changed").GetBoolean());
        using var empty = await Send(protocol,
            $"""{"requestId":"eight","action":"library","collectionId":"{{collectionId}}"}""");
        Assert.Equal(0, empty.RootElement.GetProperty("data")
            .GetProperty("items").GetArrayLength());
        await catalog.VerifyOriginalAsync(saved.ItemId);
    }

    [Theory]
    [InlineData("""{"requestId":"x","action":"execute","command":"rm"}""")]
    [InlineData("""{"requestId":"x","action":"library","limit":10000}""")]
    [InlineData("""{"requestId":"x","action":"preview","itemId":"invalid"}""")]
    [InlineData("""{"requestId":"x","action":"search","query":"   "}""")]
    [InlineData("""{"action":"library"}""")]
    [InlineData("""{"requestId":"x","action":"collectionCreate","name":"  "}""")]
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
