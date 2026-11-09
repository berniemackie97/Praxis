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

    [Theory]
    [InlineData("""{"requestId":"x","action":"execute","command":"rm"}""")]
    [InlineData("""{"requestId":"x","action":"library","limit":10000}""")]
    [InlineData("""{"requestId":"x","action":"preview","itemId":"invalid"}""")]
    [InlineData("""{"requestId":"x","action":"search","query":"   "}""")]
    [InlineData("""{"action":"library"}""")]
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
