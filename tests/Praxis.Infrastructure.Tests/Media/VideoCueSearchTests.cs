using System.Text;
using System.Text.Json;
using Praxis.Application.Desktop;
using Praxis.Application.Media;
using Praxis.Domain.Media;
using Praxis.Domain.Originals;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Media;

public sealed class VideoCueSearchTests : IDisposable
{
    private readonly string workspace = Path.Combine(
        TestPaths.Temp, "praxis-evidence-" + Guid.NewGuid().ToString("N"));
    private readonly string dataRoot;

    public VideoCueSearchTests()
    {
        dataRoot = Path.Combine(workspace, "data");
        Directory.CreateDirectory(dataRoot);
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                       UnixFileMode.UserExecute;
            File.SetUnixFileMode(workspace, privateMode);
            File.SetUnixFileMode(dataRoot, privateMode);
        }
    }

    [Theory]
    [InlineData(".vtt", "WEBVTT\n\n00:00:05.000 --> 00:00:08.000\nUse a violet soldering jig.\n\n00:00:09.000 --> 00:00:12.000\nUnplug the iron.\n")]
    [InlineData(".srt", "1\n00:00:05,000 --> 00:00:08,000\nUse a violet soldering jig.\n\n2\n00:00:09,000 --> 00:00:12,000\nUnplug the iron.\n")]
    public async Task DigestBoundVideoCaptionReturnsExactTimeAndFullMediaIdentity(
        string extension, string caption)
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        CapturedOriginal media = await Capture(catalog, "tutorial.mp4", "video fixture", "media");
        CapturedOriginal sidecar = await Capture(catalog,
            "tutorial.mp4." + media.Sha256 + extension, caption, "captions");
        ProcessingDriver processor = ProcessingDriver.Text(catalog);
        Assert.True(await processor.ProcessNextAsync());
        Assert.True(await processor.ProcessNextAsync());
        VideoCueHit cue = Assert.Single(new MediaEvidence(catalog.Sources, catalog.Search, catalog.Text, catalog.Originals).SearchCues("violet"));
        Assert.Equal(5_000L, cue.StartMilliseconds);
        Assert.Equal(8_000L, cue.EndMilliseconds);
        Assert.Equal("Use a violet soldering jig.", cue.CaptionText);
        Assert.Equal("timed-caption-v1", cue.Producer);
        Assert.Equal(sidecar.SourceId, cue.CaptionSourceId);
        Assert.Equal(sidecar.Sha256, cue.CaptionSha256);
        Assert.Equal(media.ItemId, cue.VideoItemId);
        Assert.Equal(media.Sha256, cue.VideoSha256);
        Assert.Equal(caption.Substring(cue.CaptionOffset, cue.CaptionLength), cue.CaptionText);
        Assert.Empty(new MediaEvidence(catalog.Sources, catalog.Search, catalog.Text, catalog.Originals).SearchCues("violet unplug"));
        Assert.Single(catalog.Search.Query("violet"));
        await catalog.Originals.VerifyAsync(media.ItemId);
    }

    [Fact]
    public async Task VideoRevisionOrRevocationNeverReusesStaleCaptionTimestamp()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        CapturedOriginal video = await Capture(catalog, "tutorial.mp4", "video-v1", "video-v1");
        const string caption = "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nViolet physics demonstration.\n";
        CapturedOriginal cues = await Capture(catalog,
            "tutorial.mp4." + video.Sha256 + ".vtt", caption, "captions");
        ProcessingDriver processor = ProcessingDriver.Text(catalog);
        Assert.True(await processor.ProcessNextAsync());
        Assert.True(await processor.ProcessNextAsync());
        Assert.Single(new MediaEvidence(catalog.Sources, catalog.Search, catalog.Text, catalog.Originals).SearchCues("Violet"));

        CapturedOriginal replacement = await Capture(catalog, "tutorial.mp4", "video-v2", "video-v2");
        Assert.Equal(2, replacement.Revision);
        Assert.Empty(new MediaEvidence(catalog.Sources, catalog.Search, catalog.Text, catalog.Originals).SearchCues("Violet"));

        CapturedOriginal newCues = await Capture(catalog,
            "tutorial.mp4." + replacement.Sha256 + ".vtt", caption, "new-captions");
        Assert.True(await processor.ProcessNextAsync());
        Assert.True(await processor.ProcessNextAsync());
        Assert.Equal(newCues.SourceId,
            Assert.Single(new MediaEvidence(catalog.Sources, catalog.Search, catalog.Text, catalog.Originals).SearchCues("Violet")).CaptionSourceId);

        Assert.True(catalog.Sources.Revoke(replacement.SourceId));
        Assert.Empty(new MediaEvidence(catalog.Sources, catalog.Search, catalog.Text, catalog.Originals).SearchCues("Violet"));
        Assert.True(catalog.Sources.Revoke(cues.SourceId));
        await catalog.Originals.VerifyAsync(video.ItemId);
    }

    [Fact]
    public async Task VideoRequiresExplicitHashBindingAndValidCaptionSyntax()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        CapturedOriginal video = await Capture(catalog, "tutorial.mp4", "video", "video");
        await Capture(catalog, "tutorial.vtt",
            "WEBVTT\n\n00:00:01.000 --> 00:00:03.000\nViolet topic.\n", "legacy");
        await Capture(catalog, "tutorial.mp4." + video.Sha256 + ".srt",
            "1\n00:00:10,000 --> 00:00:02,000\nViolet invalid timing.\n", "invalid");
        await Capture(catalog, "tutorial.mp4." + video.Sha256 + ".vtt",
            "WEBVTT\n\n00:00:01.000 --> 00:00:05.000\nViolet first.\n\n" +
            "00:00:03.000 --> 00:00:06.000\nViolet overlapping.\n", "overlapping");
        ProcessingDriver processor = ProcessingDriver.Text(catalog);
        Assert.True(await processor.ProcessNextAsync());
        Assert.True(await processor.ProcessNextAsync());
        Assert.True(await processor.ProcessNextAsync());
        Assert.True(await processor.ProcessNextAsync());
        Assert.Empty(new MediaEvidence(catalog.Sources, catalog.Search, catalog.Text, catalog.Originals).SearchCues("Violet"));
    }

    [Fact]
    public async Task CollectionScopeRequiresBothCaptionAndVideoSourceAndRechecksRemoval()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        CapturedOriginal video = await Capture(catalog, "scoped.mp4", "video bytes", "video");
        CapturedOriginal captions = await Capture(catalog,
            "scoped.mp4." + video.Sha256 + ".vtt",
            "WEBVTT\n\n00:00:03.000 --> 00:00:07.000\nViolet scoped tutorial.\n", "caption");
        ProcessingDriver worker = ProcessingDriver.Text(catalog);
        Assert.True(await worker.ProcessNextAsync());
        Assert.True(await worker.ProcessNextAsync());

        Guid group = catalog.Collections.Create("Paired tutorial").CollectionId;
        Assert.True(catalog.Collections.AddSource(group, captions.SourceId));
        MediaEvidence media = new(catalog.Sources, catalog.Search, catalog.Text, catalog.Originals,
            catalog.Collections);
        Assert.Single(media.SearchCues("violet"));
        Assert.Empty(media.SearchCues("violet", 20, group));

        Assert.True(catalog.Collections.AddSource(group, video.SourceId));
        Assert.True(catalog.Collections.ContainsActiveSource(group, captions.SourceId));
        Assert.True(catalog.Collections.ContainsActiveSource(group, video.SourceId));
        Assert.Equal(video.SourceId, Assert.Single(media.SearchCues("violet", 20, group)).VideoSourceId);

        DesktopProtocol protocol = TestDesktop.Protocol(catalog);
        using JsonDocument scoped = JsonDocument.Parse(await protocol.HandleAsync(
            $$"""{"requestId":"scoped","action":"mediaCues","query":"violet","collectionId":"{{group:D}}"}"""));
        Assert.True(scoped.RootElement.GetProperty("ok").GetBoolean());
        Assert.Single(scoped.RootElement.GetProperty("data").GetProperty("clips").EnumerateArray());

        Assert.True(catalog.Collections.RemoveSource(group, video.SourceId));
        Assert.Empty(media.SearchCues("violet", 20, group));
        Assert.False(catalog.Collections.ContainsActiveSource(group, video.SourceId));

        Assert.True(catalog.Collections.AddSource(group, video.SourceId));
        Assert.True(catalog.Collections.Archive(group));
        Assert.Empty(media.SearchCues("violet", 20, group));
        Assert.False(catalog.Collections.ContainsActiveSource(group, captions.SourceId));
        Assert.Single(media.SearchCues("violet"));
    }

    [Fact]
    public async Task PrivateDesktopProtocolReturnsGroundedVideoCues()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        CapturedOriginal video = await Capture(catalog, "clip.mp4", "video content", "video");
        CapturedOriginal captions = await Capture(catalog, "clip.mp4." + video.Sha256 + ".vtt",
            "WEBVTT\n\n00:00:03.000 --> 00:00:06.000\nViolet assembly instruction.\n",
            "captions");
        ProcessingDriver worker = ProcessingDriver.Text(catalog);
        Assert.True(await worker.ProcessNextAsync());
        Assert.True(await worker.ProcessNextAsync());

        DesktopProtocol protocol = TestDesktop.Protocol(catalog);
        using JsonDocument result = JsonDocument.Parse(await protocol.HandleAsync(
            """{"requestId":"clip","action":"mediaCues","query":"violet"}"""));
        Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
        JsonElement cue = Assert.Single(result.RootElement.GetProperty("data")
            .GetProperty("clips").EnumerateArray());
        Assert.Equal(video.SourceId.ToString("D"), cue.GetProperty("videoSourceId").GetString());
        Assert.Equal(captions.Sha256, cue.GetProperty("captionSha256").GetString());
        Assert.Equal(3000L, cue.GetProperty("startMilliseconds").GetInt64());
    }

    private async Task<CapturedOriginal> Capture(
        SqliteCatalog catalog, string filename, string contents, string requestKey)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(contents);
        using MemoryStream source = new(bytes);
        return await catalog.Originals.CaptureFileAsync(
            Path.Combine(workspace, "incoming", filename), source,
            bytes.Length + 1, requestKey);
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }
}
