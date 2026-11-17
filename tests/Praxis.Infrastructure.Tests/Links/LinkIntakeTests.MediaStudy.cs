using System.Security.Cryptography;
using System.Text.Json;
using Praxis.Application.Inference;
using Praxis.Application.Links;
using Praxis.Application.Media;
using Praxis.Domain.Models;
using Praxis.Infrastructure.Documents;
using Praxis.Infrastructure.Links;
using Praxis.Infrastructure.Persistence;
using Xunit;

namespace Praxis.Infrastructure.Tests.Links;

public sealed partial class LinkIntakeTests
{
    [Fact]
    public void InvalidFrameHelperOutputCannotBecomeVisualEvidence()
    {
        Assert.Empty(VideoFrameReader.Parse("{bad}", 12));
        Assert.Empty(VideoFrameReader.Parse("{\"frames\":[{\"seconds\":-1,\"text\":\"x\",\"imageBase64\":\"bad\"}]}", 12));
    }

    private static WebVideoSnapshot Uncaptioned() => new(1, "https://www.youtube.com/watch?v=abcdefghijk",
        "https://www.youtube-nocookie.com/embed/abcdefghijk", "Synthetic study fixture", "Fixture", null, 60, []);

    [Fact]
    public async Task CaptionlessMediaUsesLocalSpeechAndPreservesSampledVisualEvidenceWithUnverifiedDescriptions()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        catalog.Links.SaveSettings(new LinkSettings("saved_links", [], true, true, "vision:latest"));
        StudyDownloader downloader = new(); StudyModel model = new();
        WebVideoStudy study = new(downloader, catalog.Links, new StudySpeech(), new StudyFrames(), catalog.Models, model, model);
        WebVideoSnapshot result = await study.StudyAsync(Uncaptioned(), new Uri("https://media.example/audio"), new Uri("https://media.example/video"), CancellationToken.None);
        Assert.Equal("local_speech", result.TranscriptOrigin);
        Assert.Single(result.Cues); Assert.NotNull(result.AudioSha256);
        WebVideoFrame frame = Assert.Single(result.Frames!);
        Assert.Equal("Visible test diagram.", frame.Interpretation);
        Assert.Equal("vision-digest", frame.ModelDigest);
        Assert.Equal(1, model.Calls);
        Assert.Equal([64 * 1024 * 1024, 128 * 1024 * 1024], downloader.Limits);
        var document = WebVideoReader.Read(result);
        Assert.Contains("Local speech transcription", document.Text, StringComparison.Ordinal);
        Assert.Contains("Model interpretation (unverified)", document.Text, StringComparison.Ordinal);
        Assert.All(document.Timings!, span => Assert.InRange(span.Offset + span.Length, 0, document.Text.Length));
        Assert.NotNull(WebVideoReader.Parse(JsonSerializer.SerializeToUtf8Bytes(result)));
        Assert.Null(WebVideoReader.Parse(JsonSerializer.SerializeToUtf8Bytes(result with { Frames = [frame with { ImageSha256 = new string('0', 64) }] })));
    }

    [Fact]
    public async Task MediaStudyDefaultsAndDurationBudgetsPreventAcquisitionAndInference()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        StudyDownloader downloader = new(); StudyModel model = new();
        WebVideoStudy study = new(downloader, catalog.Links, new StudySpeech(), new StudyFrames(), catalog.Models, model, model);
        WebVideoSnapshot original = Uncaptioned();
        Assert.Equal(original, await study.StudyAsync(original, new Uri("https://media.example/audio"), null, CancellationToken.None));
        Assert.Empty(downloader.Limits);
        catalog.Links.SaveSettings(new LinkSettings("saved_links", [], true, MaximumMediaMinutes: 1));
        Assert.Equal("duration_budget", (await study.StudyAsync(original with { DurationSeconds = 61 }, new Uri("https://media.example/audio"), null, CancellationToken.None)).StudyReason);
        Assert.Empty(downloader.Limits); Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task MediaConsentRevocationDuringDownloadDiscardsNewTranscriptAndFrames()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        catalog.Links.SaveSettings(new LinkSettings("saved_links", [], true, true));
        StudySpeech speech = new(); StudyModel model = new();
        StudyDownloader downloader = new(() => catalog.Links.SaveSettings(LinkSettings.Default));
        WebVideoStudy study = new(downloader, catalog.Links, speech, new StudyFrames(), catalog.Models, model, model);
        WebVideoSnapshot result = await study.StudyAsync(Uncaptioned(), new Uri("https://media.example/audio"), new Uri("https://media.example/video"), CancellationToken.None);
        Assert.Equal("settings_changed", result.StudyReason);
        Assert.Empty(result.Cues); Assert.Null(result.Frames); Assert.Equal(0, speech.Calls); Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task MissingVisionCapabilityKeepsOcrWithoutInventingAVisualInterpretation()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        catalog.Links.SaveSettings(new LinkSettings("saved_links", [], true, true, "vision:latest"));
        StudyModel model = new(vision: false);
        WebVideoStudy study = new(new StudyDownloader(), catalog.Links, new StudySpeech(), new StudyFrames(), catalog.Models, model, model);
        WebVideoSnapshot result = await study.StudyAsync(Uncaptioned(), null, new Uri("https://media.example/video"), CancellationToken.None);
        Assert.Equal("vision_model_unavailable", result.StudyReason);
        Assert.Null(Assert.Single(result.Frames!).Interpretation); Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task InvalidVisionResponseKeepsTokenCostWithoutInventingEvidence()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        catalog.Links.SaveSettings(new LinkSettings("saved_links", [], true, true, "vision:latest"));
        StudyModel model = new(response: "{}");
        WebVideoStudy study = new(new StudyDownloader(), catalog.Links, new StudySpeech(), new StudyFrames(), catalog.Models, model, model);
        WebVideoSnapshot result = await study.StudyAsync(Uncaptioned(), null, new Uri("https://media.example/video"), CancellationToken.None);
        Assert.Equal("vision_incomplete", result.StudyReason);
        Assert.Equal(20, result.VisionInputTokens); Assert.Equal(10, result.VisionOutputTokens);
        Assert.Null(Assert.Single(result.Frames!).Interpretation);
    }

    [Fact]
    public async Task StreamingMediaLimitRejectsOversizeBodiesWithoutBufferingPastTheBudget()
    {
        using ByteArrayContent source = new([1, 2, 3, 4]);
        using MemoryStream output = new();
        Assert.False(await SafeHttpFetcher.CopyBoundedAsync(source, output, 3, CancellationToken.None));
        Assert.Equal(0, output.Length);
        using ByteArrayContent exact = new([1, 2, 3]);
        Assert.True(await SafeHttpFetcher.CopyBoundedAsync(exact, output, 3, CancellationToken.None));
        Assert.Equal(3, output.Length);
    }

    private sealed class StudyDownloader(Action? changed = null) : IMediaDownloader
    {
        public List<int> Limits { get; } = [];
        public async Task<string?> DownloadAsync(Uri url, string destination, int maximumBytes, Func<Uri, bool> blocked, CancellationToken token)
        {
            if (blocked(url))
            {
                return "host_blocked";
            }
            Limits.Add(maximumBytes);
            await File.WriteAllBytesAsync(destination, [1, 2, 3], token);
            changed?.Invoke(); return null;
        }
    }

    private sealed class StudySpeech : ITranscriptionEngine
    {
        public bool IsAvailable => true;
        public int Calls { get; private set; }
        public Task<TranscriptionResult> TranscribeAsync(string path, TimeSpan budget, CancellationToken token)
        {
            Calls++; Assert.True(File.Exists(path));
            return Task.FromResult(TranscriptionResult.Recognized([new TranscriptSegment(1_000, 2_000, "A synthetic spoken instruction.")]));
        }
    }

    private sealed class StudyFrames : IVideoFrameReader
    {
        public bool IsAvailable => true;
        public Task<IReadOnlyList<WebVideoFrame>> ReadAsync(string path, int maximumFrames, CancellationToken token)
        {
            byte[] bytes = [0xff, 0xd8, 0xff, 0xd9];
            return Task.FromResult<IReadOnlyList<WebVideoFrame>>([new(3, "Observed OCR", Convert.ToBase64String(bytes), Convert.ToHexStringLower(SHA256.HashData(bytes)))]);
        }
    }

    private sealed class StudyModel(bool vision = true, string? response = null) : ILocalModelDirectory, IChatModelConnector, IChatModel
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<InstalledModel>> ListInstalledAsync(string endpoint, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<InstalledModel>>([new("vision:latest", "vision-digest", 100, new LocalModelCapabilities(true, true, false, vision))]);
        public ModelConnection Connect(ModelProfile profile) => new(this);
        public Task<ModelIdentity> DescribeAsync(CancellationToken token) => Task.FromResult(new ModelIdentity("ollama", "vision:latest", "vision-digest", null, null, null));
        public Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken token)
        {
            Calls++; Assert.Single(request.Messages[1].Images!); request.Validate();
            return Task.FromResult(new ChatResult(response ?? """{"observations":[{"index":0,"text":"Visible test diagram."}]}""", 20, 10, TimeSpan.Zero));
        }
    }
}
