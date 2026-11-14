using System.Text.Json;
using Praxis.Application.Inference;
using Praxis.Application.Media;
using Praxis.Infrastructure.Inference.Subscription;

namespace Praxis.Infrastructure.Documents;

/// <summary>
/// On-device transcription through the bundled <c>praxis-vision</c> helper (Apple SpeechAnalyzer on
/// macOS 26 and later). The helper reads the verified original through a private link, runs
/// without a shell within the caller's time budget, and returns size-limited JSON cues. Nothing
/// leaves the Mac.
/// </summary>
public sealed class SpeechTranscriptionEngine : ITranscriptionEngine
{
    private readonly string? helper;
    private readonly string? locale;

    public SpeechTranscriptionEngine(string? helperPath = null, string? locale = null)
    {
        string candidate = helperPath ?? Environment.GetEnvironmentVariable("PRAXIS_VISION_HELPER")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", VisionOcrEngine.HelperName));
        helper = OperatingSystem.IsMacOSVersionAtLeast(26) && File.Exists(candidate) ? candidate : null;
        this.locale = locale ?? Environment.GetEnvironmentVariable("PRAXIS_TRANSCRIPTION_LOCALE");
    }

    public bool IsAvailable => helper is not null;

    public async Task<TranscriptionResult> TranscribeAsync(string path, TimeSpan budget, CancellationToken cancellationToken)
    {
        if (helper is null || !OperatingSystem.IsMacOS())
        {
            return TranscriptionResult.Failed("transcription_unavailable");
        }

        string[] arguments = locale is { Length: > 0 and <= 35 } && locale.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? ["transcribe", "--locale", locale, path]
            : ["transcribe", path];
        try
        {
            CliResult result = await CliProcess.RunAsync(helper, arguments, null, Path.GetDirectoryName(path)!, budget, cancellationToken);
            return Parse(result.Stdout) ?? TranscriptionResult.Failed(result.ExitCode == 0 ? "transcription_invalid_output" : "transcription_failed");
        }
        catch (InferenceException exception)
        {
            return TranscriptionResult.Failed(exception.Failure == InferenceFailure.Timeout ? "transcription_timeout" : "transcription_failed");
        }
    }

    public static TranscriptionResult? Parse(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 });
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.String)
            {
                string code = error.GetString() ?? "failed";
                return TranscriptionResult.Failed(code.StartsWith("transcription_", StringComparison.Ordinal) ? code : "transcription_" + code);
            }

            if (!root.TryGetProperty("cues", out JsonElement cues) || cues.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            List<TranscriptSegment> segments = [];
            foreach (JsonElement cue in cues.EnumerateArray())
            {
                if (cue.TryGetProperty("start", out JsonElement start) && start.TryGetDouble(out double from) &&
                    cue.TryGetProperty("end", out JsonElement end) && end.TryGetDouble(out double to) &&
                    cue.TryGetProperty("text", out JsonElement text) && text.GetString() is { Length: > 0 } words &&
                    from >= 0 && to >= from && to < 86_400)
                {
                    segments.Add(new TranscriptSegment((long)Math.Round(from * 1000, MidpointRounding.AwayFromZero),
                        (long)Math.Round(to * 1000, MidpointRounding.AwayFromZero), words));
                }
            }

            return TranscriptionResult.Recognized(segments);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
