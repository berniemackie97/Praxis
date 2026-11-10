using System.Text.Json;
using Praxis.Storage;
using Praxis.Storage.Catalog;
using Praxis.Storage.Desktop;
using Praxis.Storage.Models;
using Praxis.Storage.Tests.Support;
using Xunit;

namespace Praxis.Storage.Tests.Models;

public sealed class LocalModelProfileTests : IDisposable
{
    private readonly string root = Path.Combine(TestPaths.Temp,
        "praxis-models-" + Guid.NewGuid().ToString("N"));

    public LocalModelProfileTests()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void ProfileIsNormalizedPersistedAndReplaced()
    {
        using (LocalCatalog catalog = LocalCatalog.Open(root))
        {
            Assert.Null(catalog.Models.Get(ModelProfile.LocalEnrichment));
            ModelProfile saved = catalog.Models.Save(new ModelProfile(ModelProfile.LocalEnrichment,
                "ollama", "http://localhost:11434", " qwen:9b ", 16_384, Enabled: true));
            Assert.Equal("http://localhost:11434/", saved.Endpoint);
            Assert.Equal("qwen:9b", saved.Model);
        }

        using LocalCatalog reopened = LocalCatalog.Open(root);
        Assert.Equal(new ModelProfile(ModelProfile.LocalEnrichment, "ollama",
            "http://localhost:11434/", "qwen:9b", 16_384, true),
            reopened.Models.Get(ModelProfile.LocalEnrichment));

        reopened.Models.Save(new ModelProfile(ModelProfile.LocalEnrichment,
            "ollama", "http://127.0.0.1:11434", "other:3b", 8_192, Enabled: false));
        Assert.Equal("other:3b", reopened.Models.Get(ModelProfile.LocalEnrichment)!.Model);
        Assert.False(reopened.Models.Get(ModelProfile.LocalEnrichment)!.Enabled);
    }

    [Theory]
    [InlineData("ollama", "http://10.0.0.5:11434", "qwen:9b", 16_384)]
    [InlineData("ollama", "http://127.0.0.1:11434/v1", "qwen:9b", 16_384)]
    [InlineData("openai", "http://127.0.0.1:11434", "qwen:9b", 16_384)]
    [InlineData("ollama", "http://127.0.0.1:11434", "two words", 16_384)]
    [InlineData("ollama", "http://127.0.0.1:11434", "qwen:9b", 1_024)]
    public void UnsafeOrUnsupportedProfilesAreRejected(
        string provider, string endpoint, string model, int context)
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        Assert.ThrowsAny<ArgumentException>(() => catalog.Models.Save(new ModelProfile(
            ModelProfile.LocalEnrichment, provider, endpoint, model, context, true)));
        Assert.Null(catalog.Models.Get(ModelProfile.LocalEnrichment));
    }

    [Fact]
    public async Task DesktopProtocolReadsAndSavesProfileAndReportsUnreachableRuntime()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        LocalDesktopProtocol protocol = new LocalDesktopProtocol(catalog);

        using (JsonDocument empty = await Send(protocol, """{"requestId":"1","action":"modelProfile"}"""))
        {
            Assert.Equal(JsonValueKind.Null, empty.RootElement.GetProperty("data").GetProperty("profile").ValueKind);
        }

        using (JsonDocument saved = await Send(protocol, """
                                                         {"requestId":"2","action":"modelProfileSave","provider":"ollama",
                                                          "endpoint":"http://127.0.0.1:11434","model":"qwen:9b","contextTokens":8192,"enabled":true}
                                                         """))
        {
            Assert.True(saved.RootElement.GetProperty("ok").GetBoolean());
            Assert.Equal(8192, saved.RootElement.GetProperty("data").GetProperty("profile")
                .GetProperty("contextTokens").GetInt32());
        }

        using (JsonDocument remote = await Send(protocol, """
                                                          {"requestId":"3","action":"modelProfileSave","provider":"ollama",
                                                           "endpoint":"http://192.168.1.2:11434","model":"qwen:9b","enabled":true}
                                                          """))
        {
            Assert.False(remote.RootElement.GetProperty("ok").GetBoolean());
        }

        // Port 9 (discard) on loopback is not an Ollama runtime: listing fails cleanly.
        using JsonDocument unreachable = await Send(protocol,
            """{"requestId":"4","action":"localModels","endpoint":"http://127.0.0.1:9"}""");
        Assert.False(unreachable.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("qwen:9b", catalog.Models.Get(ModelProfile.LocalEnrichment)!.Model);
    }

    [Fact]
    public async Task EmbeddingProfileIsOptInAndCannotAuthorizeRemoteInference()
    {
        using LocalCatalog catalog = LocalCatalog.Open(root);
        LocalDesktopProtocol protocol = new(catalog);
        Assert.Null(catalog.Models.Get(ModelProfile.LocalEmbedding));

        using JsonDocument initiallyDenied = await Send(protocol,
            """{"requestId":"one","action":"semanticIndex","rebuild":false}""");
        Assert.False(initiallyDenied.RootElement.GetProperty("ok").GetBoolean());

        using JsonDocument saved = await Send(protocol, """
            {"requestId":"two","action":"embeddingProfileSave","provider":"ollama",
             "endpoint":"http://127.0.0.1:11434","model":"embeddinggemma",
             "contextTokens":2048,"enabled":false}
            """);
        Assert.True(saved.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(ModelProfile.LocalEmbedding,
            saved.RootElement.GetProperty("data").GetProperty("profile").GetProperty("role").GetString());
        Assert.Null(catalog.Models.Get(ModelProfile.LocalEnrichment));

        using JsonDocument lexical = await Send(protocol,
            """{"requestId":"three","action":"search","query":"local"}""");
        Assert.True(lexical.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("lexical", lexical.RootElement.GetProperty("data").GetProperty("mode").GetString());
        using JsonDocument stillDenied = await Send(protocol,
            """{"requestId":"four","action":"semanticIndex","rebuild":false}""");
        Assert.False(stillDenied.RootElement.GetProperty("ok").GetBoolean());

        using JsonDocument remote = await Send(protocol, """
            {"requestId":"five","action":"embeddingProfileSave","provider":"ollama",
             "endpoint":"http://192.168.1.2:11434","model":"embeddinggemma",
             "contextTokens":2048,"enabled":true}
            """);
        Assert.False(remote.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(catalog.Models.Get(ModelProfile.LocalEmbedding)!.Enabled);
    }

    private static async Task<JsonDocument> Send(LocalDesktopProtocol protocol, string request) =>
        JsonDocument.Parse(await protocol.HandleAsync(request));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
