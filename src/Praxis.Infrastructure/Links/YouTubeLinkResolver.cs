using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Praxis.Application.Inference;
using Praxis.Application.Links;
using Praxis.Infrastructure.Inference.Subscription;

namespace Praxis.Infrastructure.Links;

/// <summary>
/// Saved YouTube links become web-video snapshots: public metadata and, when the owner has
/// <c>yt-dlp</c> installed, the published captions (subtitles only; the video is not downloaded).
/// Without <c>yt-dlp</c> the public oEmbed metadata is kept so the link is still searchable. Playback
/// uses the privacy-enhanced embed (youtube-nocookie.com).
/// </summary>
public sealed partial class YouTubeLinkResolver(ILinkFetcher fetcher, string? ytDlpPath = null, IWebVideoStudy? study = null) : IVideoLinkResolver
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(3);
    private readonly string? ytDlp = ytDlpPath ?? Locate();

    public bool Handles(Uri url) => VideoId(url) is not null;

    public async Task<WebVideoSnapshot?> ResolveAsync(Uri url, CancellationToken cancellationToken)
    {
        if (VideoId(url) is not { } id)
        {
            return null;
        }

        string canonical = $"https://www.youtube.com/watch?v={id}";
        string embed = $"https://www.youtube-nocookie.com/embed/{id}";
        if (ytDlp is not null && await CaptionsAsync(canonical, embed, cancellationToken) is { } snapshot)
        {
            return snapshot;
        }

        FetchedPage page = await fetcher.FetchAsync(new Uri("https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString(canonical)), cancellationToken);
        if (page.Body is null)
        {
            return null;
        }

        try
        {
            using JsonDocument meta = JsonDocument.Parse(page.Body);
            string title = meta.RootElement.TryGetProperty("title", out JsonElement value) ? value.GetString() ?? id : id;
            string? channel = meta.RootElement.TryGetProperty("author_name", out JsonElement author) ? author.GetString() : null;
            return new WebVideoSnapshot(WebVideoSnapshot.CurrentVersion, canonical, embed, title, channel, null, null, []);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The 11-character video id of a YouTube watch, short, shorts or embed URL.</summary>
    public static string? VideoId(Uri url)
    {
        string host = url.Host.ToLowerInvariant();
        string? id = host switch
        {
            "youtu.be" => url.AbsolutePath.Trim('/').Split('/')[0],
            "www.youtube.com" or "youtube.com" or "m.youtube.com" or "music.youtube.com" or "www.youtube-nocookie.com" =>
                url.AbsolutePath == "/watch"
                    ? Query(url, "v")
                    : url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is [("shorts" or "embed" or "live" or "v"), var segment, ..] ? segment : null,
            _ => null
        };
        return id is not null && Id().IsMatch(id) ? id : null;
    }

    /// <summary>Turns WebVTT captions (including rolling auto-captions) into clean, non-repeating cues.</summary>
    public static IReadOnlyList<WebVideoCue> ParseCaptions(string vtt)
    {
        List<WebVideoCue> cues = [];
        string? lastLine = null;
        foreach (string block in vtt.Replace("\r", string.Empty, StringComparison.Ordinal).Split("\n\n"))
        {
            string[] lines = block.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            int timing = Array.FindIndex(lines, line => line.Contains("-->", StringComparison.Ordinal));
            if (timing < 0 || !TryTiming(lines[timing], out double start, out double end) || end - start < 0.05)
            {
                continue;
            }

            StringBuilder text = new();
            foreach (string raw in lines.Skip(timing + 1))
            {
                string line = Tags().Replace(raw, string.Empty).Trim();
                line = System.Net.WebUtility.HtmlDecode(line);
                if (line.Length == 0 || line == lastLine)
                {
                    continue;
                }

                text.Append(text.Length > 0 ? " " : string.Empty).Append(line);
                lastLine = line;
            }

            if (text.Length > 0)
            {
                cues.Add(new WebVideoCue(start, end, text.ToString()));
            }
        }

        return cues;
    }

    private async Task<WebVideoSnapshot?> CaptionsAsync(string canonical, string embed, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        string directory = Path.Combine(Path.GetTempPath(), "praxis-ytdlp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            string[] arguments =
            [
                // Explicit languages: a wildcard also requests every machine translation and gets rate-limited.
                "--ignore-config", "--ignore-errors", "--no-playlist", "--skip-download", "--write-info-json", "--write-subs", "--write-auto-subs",
                "--sub-langs", "en,en-US,en-GB,en-orig", "--sub-format", "vtt", "--no-warnings", "--no-progress", "-o", Path.Combine(directory, "video"), "--", canonical
            ];
            _ = await CliProcess.RunAsync(ytDlp!, arguments, null, directory, Budget, cancellationToken);
            string info = Path.Combine(directory, "video.info.json");
            // A missing caption track is not fatal; the metadata file decides.
            if (!File.Exists(info) || new FileInfo(info).Length > 8 * 1024 * 1024)
            {
                return null;
            }

            using JsonDocument meta = JsonDocument.Parse(await File.ReadAllBytesAsync(info, cancellationToken));
            JsonElement root = meta.RootElement;
            string title = Text(root, "title") ?? "YouTube video";
            string? description = Text(root, "description") is { } full ? (full.Length > 4_000 ? full[..4_000] + "…" : full) : null;
            double? duration = root.TryGetProperty("duration", out JsonElement seconds) && seconds.TryGetDouble(out double value) ? value : null;
            // Prefer creator captions over automatic ones; both are written as video.<lang>.vtt.
            string? captions = new[] { "en", "en-US", "en-GB", "en-orig" }.Select(language => Path.Combine(directory, $"video.{language}.vtt"))
                .FirstOrDefault(File.Exists);
            IReadOnlyList<WebVideoCue> cues = captions is not null && new FileInfo(captions).Length <= 8 * 1024 * 1024
                ? ParseCaptions(await File.ReadAllTextAsync(captions, cancellationToken))
                : [];
            WebVideoSnapshot captured = new(WebVideoSnapshot.CurrentVersion, canonical, embed, title, Text(root, "channel") ?? Text(root, "uploader"),
                description, duration, cues, cues.Count == 0 ? null : "published_captions");
            return study is null ? captured : await study.StudyAsync(captured, DirectMedia(root, audio: true), DirectMedia(root, audio: false), cancellationToken);
        }
        catch (InferenceException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // The system removes leftover temporary folders.
            }
        }
    }

    private static Uri? DirectMedia(JsonElement root, bool audio)
    {
        if (!root.TryGetProperty("formats", out JsonElement formats) || formats.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        return formats.EnumerateArray().Where(format => Text(format, "protocol") is "https" or "http")
                    .Where(format => audio ? Text(format, "ext") == "m4a" && Text(format, "vcodec") == "none"
                        : Text(format, "ext") == "mp4" && Text(format, "vcodec") is not (null or "none") &&
                            format.TryGetProperty("height", out JsonElement height) && height.TryGetInt32(out int pixels) && pixels <= 480)
                    .Select(format => SavedLink.Parse(Text(format, "url"))).FirstOrDefault(url => url is not null);
    }

    private static string? Locate() =>
        Environment.GetEnvironmentVariable("PRAXIS_YTDLP") is { Length: > 0 } configured && File.Exists(configured)
            ? configured
            : new[] { "/opt/homebrew/bin/yt-dlp", "/usr/local/bin/yt-dlp" }.FirstOrDefault(File.Exists);

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Query(Uri url, string name) => url.Query.TrimStart('?').Split('&')
        .Select(pair => pair.Split('=', 2)).FirstOrDefault(pair => pair[0] == name && pair.Length == 2)?[1];

    private static bool TryTiming(string line, out double start, out double end)
    {
        start = end = 0;
        string[] parts = line.Split("-->", StringSplitOptions.TrimEntries);
        return parts.Length == 2 && TrySeconds(parts[0], out start) && TrySeconds(parts[1].Split(' ')[0], out end) && end >= start;
    }

    private static bool TrySeconds(string value, out double seconds)
    {
        seconds = 0;
        string[] parts = value.Split(':');
        double total = 0;
        foreach (string part in parts)
        {
            if (!double.TryParse(part.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double number))
            {
                return false;
            }

            total = (total * 60) + number;
        }

        seconds = total;
        return parts.Length is 2 or 3;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{11}$")]
    private static partial Regex Id();

    [GeneratedRegex("<[^>]{0,200}>")]
    private static partial Regex Tags();
}
