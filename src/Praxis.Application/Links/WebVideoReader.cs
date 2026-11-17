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
        try
        {
            WebVideoSnapshot? snapshot = JsonSerializer.Deserialize<WebVideoSnapshot>(bytes, new JsonSerializerOptions { MaxDepth = 6 });
            return snapshot is { Version: WebVideoSnapshot.CurrentVersion, Title.Length: > 0 } && SavedLink.Parse(snapshot.Url) is not null &&
                   SavedLink.Parse(snapshot.EmbedUrl) is not null && snapshot.Cues is not null
                ? snapshot
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
            : "Coverage: Published captions. Visual content has not been analyzed; captions may omit speech.\n\n");
        if (snapshot.Description is { Length: > 0 } description)
        {
            header.Append(description.Trim()).Append("\n\n");
        }

        (string transcript, IReadOnlyList<TimedSpan> timings) = TranscriptText.Compose(snapshot.Cues
            .Where(cue => cue.Start >= 0 && cue.End >= cue.Start && cue.End < 86_400)
            .Select(cue => new TranscriptSegment((long)Math.Round(cue.Start * 1000), (long)Math.Round(cue.End * 1000), cue.Text)));
        int offset = header.Length;
        string text = transcript.Length == 0 ? header.ToString().TrimEnd() : header.Append(transcript).ToString();
        return new ReadDocument(DocumentFormats.WebVideo, text, Title: snapshot.Title.Trim(),
            Timings: timings.Select(span => span with { Offset = span.Offset + offset }).ToArray());
    }
}
