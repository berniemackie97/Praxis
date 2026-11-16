using System.Text;
using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Runtime;
using Praxis.Application.Agents.Tools;
using Praxis.Application.Media;
using Praxis.Application.Search;
using Praxis.Application.Text;
using Praxis.Domain.Agents;
using Praxis.Domain.Media;
using Praxis.Domain.Originals;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Documents;
using Praxis.Infrastructure.Inference.Ollama;
using Praxis.Infrastructure.Persistence;
using Praxis.Infrastructure.Tests.Agents;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Media;

public sealed class MediaMomentTests : IDisposable
{
    private static readonly TranscriptSegment[] Tutorial =
    [
        new(0, 1_620, "Welcome to the tutorial."),
        new(1_620, 3_960, "First, we install the package."),
        new(3_960, 6_780, "Then we configure the database connection string."),
        new(6_780, 10_320, "Finally, we run the migrations and start the server."),
        new(40_000, 44_000, "Questions about deployment come at the end.")
    ];

    private readonly string workspace = Path.Combine(TestPaths.Temp, "praxis-moments-" + Guid.NewGuid().ToString("N"));
    private readonly string dataRoot;

    public MediaMomentTests()
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

    [Fact]
    public void TranscriptIsReadableProseWithExactTiming()
    {
        (string text, IReadOnlyList<TimedSpan> timings) = TranscriptText.Compose(Tutorial);

        Assert.StartsWith("Welcome to the tutorial. First, we install the package.", text, StringComparison.Ordinal);
        Assert.Contains("start the server.\n\nQuestions about deployment", text, StringComparison.Ordinal);
        Assert.Equal(Tutorial.Length, timings.Count);
        Assert.All(timings.Zip(Tutorial), pair => Assert.Equal(pair.Second.Text, text.Substring(pair.First.Offset, pair.First.Length)));
        Assert.Equal(3_960, timings[2].StartMilliseconds);
    }

    [Fact]
    public void TimeLocatorsRoundTrip()
    {
        string locator = TimeLocator.Format(2_460, 11_320);
        Assert.Equal("time:2.46-11.32", locator);
        Assert.True(TimeLocator.TryParse(locator, out long start, out long end));
        Assert.Equal((2_460L, 11_320L), (start, end));
        Assert.False(TimeLocator.TryParse("time:5-2", out _, out _));
        Assert.False(TimeLocator.TryParse("chars:1-2", out _, out _));
    }

    [Fact]
    public async Task VideoIsTranscribedOnDeviceAndCutIntoCitableMoments()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        CapturedOriginal video = await Capture(catalog, "setup tutorial.mp4", "not really a video");
        FakeTranscriber transcriber = new(TranscriptionResult.Recognized(Tutorial));
        ProcessingDriver reader = new(catalog, new TextExtractionHandler(catalog.Text, catalog.Originals, catalog.Processing,
            CatalogProcessing.Readers(new VisionOcrEngine()), transcriber));

        Assert.True(await reader.ProcessNextAsync());

        Assert.Equal(".mp4", Path.GetExtension(transcriber.Path));
        Assert.False(File.Exists(transcriber.Path), "The materialized link is released after transcription.");
        Assert.Equal(Tutorial.Length, catalog.Text.ReadTimings(video.ItemId, video.Revision).Count);
        Assert.Equal("transcript", catalog.Understanding.ReadCurrent(video.SourceId)?.Format);

        MediaMoments moments = new(catalog.Text);
        MediaFindTool tool = new(new KnowledgeRetriever(catalog.Search, catalog.Embeddings, catalog.Models, new OllamaEmbeddingModelConnector()),
            moments, catalog.Sources);
        AgentToolOutput output = await tool.InvokeAsync(Json("""{"query":"configure database connection"}"""), AgentPrimitivesTests.Context(),
            CancellationToken.None);

        Praxis.Domain.Agents.AgentCitation citation = Assert.Single(output.Citations);
        Assert.Equal(video.SourceId, citation.SourceId);
        Assert.Equal(MediaPlayback.AudioSampleTimebase, citation.PlaybackTimebase);
        Assert.True(TimeLocator.TryParse(citation.Locator, out long start, out long end));
        Assert.Equal(3_960 - MediaMoments.LeadInMilliseconds, start);
        Assert.True(end - start >= MediaMoments.MinimumMilliseconds);
        Assert.Contains("configure the database connection string", output.Content, StringComparison.Ordinal);

        AgentToolOutput whole = await tool.InvokeAsync(Json($$"""{"sourceId":"{{video.SourceId}}"}"""), AgentPrimitivesTests.Context(), CancellationToken.None);
        Assert.Equal(TimeLocator.Format(0, 44_000 + MediaMoments.TailMilliseconds), Assert.Single(whole.Citations).Locator);

        KnowledgeOpenTool open = new(catalog.Text, catalog.Sources, catalog.Originals, new PdfPigTextReader(), moments);
        AgentToolOutput opened = await open.InvokeAsync(Json($$"""{"sourceId":"{{video.SourceId}}","locator":"time:40-44"}"""),
            AgentPrimitivesTests.Context(), CancellationToken.None);
        Assert.StartsWith("Questions about deployment", JsonDocument.Parse(opened.Content).RootElement.GetProperty("text").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SilentRecordingIsReadWithoutTextAndUnavailableTranscriptionDefers()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        CapturedOriginal silent = await Capture(catalog, "ambient.m4a", "silence");
        ProcessingDriver reader = new(catalog, new TextExtractionHandler(catalog.Text, catalog.Originals, catalog.Processing,
            CatalogProcessing.Readers(new VisionOcrEngine()), new FakeTranscriber(TranscriptionResult.Recognized([]))));
        Assert.True(await reader.ProcessNextAsync());
        Assert.Equal("no_speech", catalog.Text.ReadCurrent(silent.ItemId, silent.Revision)?.ReasonCode);

        CapturedOriginal pending = await Capture(catalog, "talk.mp3", "speech");
        ProcessingDriver unavailable = new(catalog, new TextExtractionHandler(catalog.Text, catalog.Originals, catalog.Processing,
            CatalogProcessing.Readers(new VisionOcrEngine()), new FakeTranscriber(TranscriptionResult.Failed("transcription_speech_model_unavailable"))));
        await Assert.ThrowsAsync<Praxis.Application.Processing.ProcessorUnavailableException>(() => unavailable.ProcessNextAsync());
        Assert.Null(catalog.Text.ReadCurrent(pending.ItemId, pending.Revision));
    }

    [Fact]
    public async Task PlaybackResolvesTheVerifiedOriginalUnderItsContainerExtension()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        CapturedOriginal video = await Capture(catalog, "talk.mov", "movie bytes");
        MediaPlayback playback = new(catalog.Sources, catalog.Originals, new MediaEvidence(catalog.Sources, catalog.Search, catalog.Text, catalog.Originals));

        MediaPlaybackReference reference = await playback.ResolveAsync(video.SourceId, CancellationToken.None);

        Assert.Equal(("file", "video/quicktime", true), (reference.Kind, reference.MimeType, reference.IsVideo));
        Assert.EndsWith(".mov", reference.Path, StringComparison.Ordinal);
        Assert.Equal("movie bytes", await File.ReadAllTextAsync(reference.Path!));
        catalog.Originals.ReleaseMaterialized(reference.Path!);
        Assert.False(File.Exists(reference.Path));
    }

    [Fact]
    public async Task CitedVideoCannotPlayAnUnverifiedClockOrReplacementRevision()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        CapturedOriginal first = await Capture(catalog, "movable.mp4", "original bytes");
        MediaPlayback playback = new(catalog.Sources, catalog.Originals, new MediaEvidence(catalog.Sources, catalog.Search, catalog.Text, catalog.Originals));
        await Assert.ThrowsAsync<InvalidOperationException>(() => playback.ResolveAsync(first.SourceId, CancellationToken.None,
            first.ItemId, first.Revision, citedClip: true));

        MediaPlaybackReference playable = await playback.ResolveAsync(first.SourceId, CancellationToken.None,
            first.ItemId, first.Revision, MediaPlayback.AudioSampleTimebase, citedClip: true);
        Assert.Equal("original bytes", await File.ReadAllTextAsync(playable.Path!));

        CapturedOriginal second = await Capture(catalog, "movable.mp4", "replacement bytes", "replacement revision");
        Assert.Equal(first.SourceId, second.SourceId);
        Assert.NotEqual(first.Revision, second.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => playback.ResolveAsync(first.SourceId, CancellationToken.None,
            first.ItemId, first.Revision, MediaPlayback.AudioSampleTimebase, citedClip: true));
    }


    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlainLanguageClipRequestFindsTimedRamPassageWithoutAProvider(bool hasMatch)
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        CapturedOriginal video = await Capture(catalog, "computers.mp4", "fixture movie");
        string spoken = hasMatch ? "RAM keeps temporary data available for the processor." : "The ramp makes a cabinet easier to load.";
        ProcessingDriver reader = new(catalog, new TextExtractionHandler(catalog.Text, catalog.Originals, catalog.Processing,
            CatalogProcessing.Readers(new VisionOcrEngine()), new FakeTranscriber(TranscriptionResult.Recognized([new(16_000, 21_000, spoken)]))));
        Assert.True(await reader.ProcessNextAsync());

        MediaFindTool tool = new(new KnowledgeRetriever(catalog.Search, catalog.Embeddings, catalog.Models, new OllamaEmbeddingModelConnector()),
            new MediaMoments(catalog.Text), catalog.Sources);
        AgentBackendProfile local = new("ollama", AgentBackendKinds.OpenAiCompatible, "http://127.0.0.1:11434/v1", "tiny-fixture", null, null, true);
        catalog.Agents.SaveBackend(local);
        AgentConversation conversation = catalog.Agents.Create(local.BackendId, "previous and new topic");
        catalog.Agents.Append(conversation.ConversationId, AgentMessage.FromUser("What company does the owner run?"));
        catalog.Agents.AppendFinal(conversation.ConversationId, AgentMessage.FromAssistant("An entirely different topic.", []));
        AgentRuntime chat = AgentComposition.Runtime(catalog, [tool], new UnavailableBackendFactory(), TimeProvider.System);

        AgentReply reply = await chat.RunAsync(conversation.ConversationId, "can you give me a clip on how ram works", (_, _, _) => { },
            CancellationToken.None, useTools: false);

        Assert.Equal(0, reply.InputTokens);
        Assert.DoesNotContain("company", reply.Text, StringComparison.Ordinal);
        Assert.Single(catalog.Agents.ReadToolCalls(conversation.ConversationId, 10));
        if (!hasMatch)
        {
            Assert.Empty(reply.Citations);
            Assert.Contains("couldn’t find a playable clip", reply.Text, StringComparison.Ordinal);
            return;
        }

        AgentCitation clip = Assert.Single(reply.Citations);
        Assert.Equal(video.SourceId, clip.SourceId);
        Assert.Equal("time:14.5-44.5", clip.Locator);
        Assert.Contains("[1]", reply.Text, StringComparison.Ordinal);
        Assert.Equal(clip, Assert.Single(catalog.Agents.ReadMessages(conversation.ConversationId)[^1].Citations));
    }

    private static readonly TranscriptSegment[] Lecture =
    [
        new(0, 5_000, "Today we look at how a computer is built."),
        new(5_000, 10_000, "The RAM chip sits next to the processor on the board."),
        new(10_000, 15_000, "But first, the power supply."),
        new(15_000, 20_000, "It converts wall current into steady voltage."),
        new(20_000, 25_000, "Every part depends on it."),
        new(25_000, 30_000, "Fans keep it cool."),
        new(30_000, 35_000, "That covers power."),
        new(35_000, 40_000, "Cables carry it to each part."),
        new(40_000, 45_000, "Let us move on."),
        new(45_000, 50_000, "Now for memory."),
        new(50_000, 55_000, "Random access memory holds the data the processor is working on,"),
        new(55_000, 60_000, "and because it is random access memory, any address can be read just as fast."),
        new(60_000, 65_000, "RAM forgets everything when the power goes off."),
        new(65_000, 70_000, "That is why RAM is paired with storage."),
        new(70_000, 75_000, "Each RAM cell is a tiny latch,"),
        new(75_000, 80_000, "and the address picks one row of latches."),
        new(80_000, 85_000, "That is the whole trick."),
        new(85_000, 90_000, "Next, the disk.")
    ];

    [Fact]
    public async Task ClipsCoverTheDensestPassageInWholeSentencesAndFollowUpsGetDifferentOnes()
    {
        using SqliteCatalog catalog = SqliteCatalog.Open(dataRoot);
        CapturedOriginal video = await Capture(catalog, "How-do-computers-work.mp4", "fixture movie");
        ProcessingDriver reader = new(catalog, new TextExtractionHandler(catalog.Text, catalog.Originals, catalog.Processing,
            CatalogProcessing.Readers(new VisionOcrEngine()), new FakeTranscriber(TranscriptionResult.Recognized(Lecture))));
        Assert.True(await reader.ProcessNextAsync());

        MediaFindTool tool = new(new KnowledgeRetriever(catalog.Search, catalog.Embeddings, catalog.Models, new OllamaEmbeddingModelConnector()),
            new MediaMoments(catalog.Text), catalog.Sources, catalog.Agents);
        AgentBackendProfile local = new("ollama", AgentBackendKinds.OpenAiCompatible, "http://127.0.0.1:11434/v1", "tiny-fixture", null, null, true);
        catalog.Agents.SaveBackend(local);
        AgentConversation conversation = catalog.Agents.Create(local.BackendId, "ram clips");
        AgentRuntime chat = AgentComposition.Runtime(catalog, [tool], new UnavailableBackendFactory(), TimeProvider.System);

        AgentReply first = await chat.RunAsync(conversation.ConversationId, "show me a clip of how ram works", (_, _, _) => { },
            CancellationToken.None, useTools: false);

        // The passage that explains RAM (spoken out as "random access memory" too) wins over the passing first mention,
        // starts at its sentence and runs to the end of the sentence it was in.
        AgentCitation best = first.Citations[0];
        Assert.True(TimeLocator.TryParse(best.Locator, out long start, out long end));
        Assert.Equal(50_000 - MediaMoments.LeadInMilliseconds, start);
        Assert.Equal(80_000 + MediaMoments.TailMilliseconds, end);
        Assert.Equal(2, first.Citations.Count);

        AgentReply refined = await chat.RunAsync(conversation.ConversationId, "those clips are bad do you have other ones?", (_, _, _) => { },
            CancellationToken.None, useTools: false);
        Assert.Equal(0, refined.InputTokens);
        Assert.Contains("different", refined.Text, StringComparison.Ordinal);
        Assert.All(refined.Citations, citation => Assert.DoesNotContain(citation.Locator, first.Citations.Select(item => item.Locator)));

        AgentReply exhausted = await chat.RunAsync(conversation.ConversationId, "same video is fine just make the clips better", (_, _, _) => { },
            CancellationToken.None, useTools: false);
        Assert.Empty(exhausted.Citations);
        Assert.Contains("beyond the ones I already showed", exhausted.Text, StringComparison.Ordinal);

        // A model that names the recording by its file name gets it, and can ask to include clips already shown.
        AgentToolOutput named = await tool.InvokeAsync(Json("""{"query":"ram","sourceId":"How-do-computers-work.mp4","includeShown":true}"""),
            AgentPrimitivesTests.Context() with { ConversationId = conversation.ConversationId }, CancellationToken.None);
        Assert.Equal(video.SourceId, named.Citations[0].SourceId);
        Assert.Equal(best.Locator, named.Citations[0].Locator);
        ArgumentException unknown = await Assert.ThrowsAsync<ArgumentException>(() => tool.InvokeAsync(
            Json("""{"query":"ram","sourceId":"missing.mp4"}"""), AgentPrimitivesTests.Context(), CancellationToken.None));
        Assert.Contains("No recording named 'missing.mp4'", unknown.Message, StringComparison.Ordinal);
    }

    private sealed class UnavailableBackendFactory : IAgentBackendFactory
    {
        public IAgentBackend? CreateTurnBackend(AgentBackendProfile profile) => throw new InvalidOperationException("No model should be called for this request.");
        public ISessionAgentBackend? CreateSessionBackend(AgentBackendProfile profile) => throw new InvalidOperationException("No model should be called for this request.");
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private async Task<CapturedOriginal> Capture(SqliteCatalog catalog, string filename, string contents, string? requestKey = null)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(contents);
        using MemoryStream source = new(bytes);
        return await catalog.Originals.CaptureFileAsync(Path.Combine(workspace, "incoming", filename), source, bytes.Length + 1, requestKey ?? filename);
    }

    public void Dispose()
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    private sealed class FakeTranscriber(TranscriptionResult result) : ITranscriptionEngine
    {
        public string? Path { get; private set; }

        public bool IsAvailable => true;

        public Task<TranscriptionResult> TranscribeAsync(string path, TimeSpan budget, CancellationToken cancellationToken)
        {
            Path = path;
            Assert.True(File.Exists(path));
            return Task.FromResult(result);
        }
    }
}
