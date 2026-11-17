using System.Text;
using System.Text.Json;
using Praxis.Application.Documents;
using Praxis.Application.Media;
using Praxis.Domain.Media;

namespace Praxis.Application.Links;

/// <summary>
/// Reads a saved online video (<c>.webvideo</c>): title, channel and description, then its captions
/// as readable transcript text with timing, so moments work on linked videos without downloading them.
/// </summary>
public sealed class WebVideoReader : IDocumentReader
{
    private const int MaximumBytes = 8 * 1024 * 1024;

    public IReadOnlyCollection<string> Extensions { get; } = [WebVideoSnapshot.Extension];

    public bool CanRead(DocumentInput input) => input.Extension == WebVideoSnapshot.Extension && input.Bytes.Length <= MaximumBytes;

    public Task<ReadOutcome> ReadAsync(DocumentInput input, CancellationToken cancellationToken) =>
        Task.FromResult(Parse(input.Bytes) is { } snapshot ? ReadOutcome.Read(Read(snapshot)) : ReadOutcome.Unreadable("web_video_unreadable"));

    public static WebVideoSnapshot? Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes)
        {
            return null;
        }
        try
        {
            WebVideoSnapshot? snapshot = JsonSerializer.Deserialize<WebVideoSnapshot>(bytes, new JsonSerializerOptions { MaxDepth = 6 });
            return snapshot is { Version: WebVideoSnapshot.CurrentVersion, Title.Length: > 0 } && SavedLink.Parse(snapshot.Url) is not null &&
                   SavedLink.Parse(snapshot.EmbedUrl) is not null && snapshot.Cues is not null &&
                   (snapshot.Frames is null || snapshot.Frames.Count <= 12 && snapshot.Frames.All(ValidFrame))
                ? snapshot
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool ValidFrame(WebVideoFrame frame)
    {
        if (frame is null || !double.IsFinite(frame.Seconds) || frame.Seconds is < 0 or >= 86_400 ||
            frame.Text is not { Length: <= 8_000 } || frame.ImageBase64 is not { Length: > 0 and <= 140_000 } ||
            frame.ImageSha256 is not { Length: 64 } || frame.Interpretation is { Length: > 600 })
        {
            return false;
        }
        byte[] data = new byte[105_000];
        return Convert.TryFromBase64String(frame.ImageBase64, data, out int length) && length is >= 3 and <= 100_000 && data[0] == 0xff && data[1] == 0xd8 && data[2] == 0xff &&
            frame.ImageSha256 == Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(data.AsSpan(0, length)));
    }

    public static ReadDocument Read(WebVideoSnapshot snapshot)
    {
        StringBuilder header = new();
        header.Append("# ").Append(snapshot.Title.Trim()).Append("\n\n");
        if (snapshot.Channel is { Length: > 0 } channel)
        {
            header.Append("Channel: ").Append(channel.Trim()).Append('\n');
        }

        header.Append("Video: ").Append(snapshot.Url).Append("\n\n");
        header.Append(snapshot.Cues.Count == 0
            ? "Coverage: Metadata only. No transcript was captured; spoken content has not been read.\n\n"
            : snapshot.TranscriptOrigin == "local_speech"
                ? "Coverage: Local speech transcription. Recognition may contain errors; verify exact wording against the source.\n\n"
                : "Coverage: Published captions. Visual content has not been analyzed; captions may omit speech.\n\n");
        if (snapshot.Frames is not { Count: > 0 })
        {
            header.Append("Visual coverage: No source frames were captured.\n\n");
        }
        if (snapshot.Frames is { Count: > 0 })
        {
            header.Append("Visual coverage: Sampled frames only. Unsampled intervals are unknown. Model interpretations are unverified.\n\n");
        }
        if (snapshot.StudyReason is { } reason)
        {
            header.Append("Study status: ").Append(reason).Append("\n\n");
        }
        if (snapshot.Description is { Length: > 0 } description)
        {
            header.Append(description.Trim()).Append("\n\n");
        }

        (string transcript, IReadOnlyList<TimedSpan> timings) = TranscriptText.Compose(snapshot.Cues
            .Where(cue => cue.Start >= 0 && cue.End >= cue.Start && cue.End < 86_400)
            .Select(cue => new TranscriptSegment((long)Math.Round(cue.Start * 1000), (long)Math.Round(cue.End * 1000), cue.Text)));
        int offset = header.Length;
        header.Append(transcript);
        List<TimedSpan> indexed = timings.Select(span => span with { Offset = span.Offset + offset }).ToList();
        foreach (WebVideoFrame frame in snapshot.Frames ?? [])
        {
            header.Append("\n\n");
            int start = header.Length;
            header.Append("Sampled frame at ").Append(frame.Seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))
                .Append(" seconds. Source image SHA-256: ").Append(frame.ImageSha256).Append('\n');
            if (frame.Text.Length > 0)
            {
                header.Append("On-screen OCR: ").Append(frame.Text).Append('\n');
            }
            if (frame.Interpretation is { } interpretation)
            {
                header.Append("Model interpretation (unverified): ").Append(interpretation).Append('\n');
            }
            long time = (long)Math.Round(frame.Seconds * 1000);
            indexed.Add(new TimedSpan(start, header.Length - start, time, time + 1000));
        }
        return new ReadDocument(DocumentFormats.WebVideo, header.ToString(), Title: snapshot.Title.Trim(), Timings: indexed);
    }
}
