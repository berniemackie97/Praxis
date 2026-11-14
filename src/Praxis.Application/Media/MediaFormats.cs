namespace Praxis.Application.Media;

/// <summary>Audio and video containers the on-device transcriber reads, and which of them carry pictures.</summary>
public static class MediaFormats
{
    /// <summary>The read stage's format name for a transcript stored as WebVTT.</summary>
    public const string Transcript = "transcript";

    private static readonly HashSet<string> Video = new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mov", ".m4v" };

    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
    {
        ".m4a", ".mp3", ".wav", ".aac", ".aif", ".aiff", ".caf", ".flac", ".m4b"
    };

    /// <summary>Extensions whose speech is transcribed by the read stage.</summary>
    public static IReadOnlyList<string> TranscribableExtensions { get; } = [.. Video.Order(StringComparer.Ordinal), .. Audio.Order(StringComparer.Ordinal)];

    public static bool IsTranscribable(string extension) => Video.Contains(extension) || Audio.Contains(extension);

    public static bool IsVideo(string extension) => Video.Contains(extension) || extension.Equals(".webm", StringComparison.OrdinalIgnoreCase)
                                                    || extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase);

    public static bool IsAudio(string extension) => Audio.Contains(extension);

    /// <summary>The MIME type a player needs when the file it opens has no extension.</summary>
    public static string MimeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" => "video/mp4",
        ".mov" => "video/quicktime",
        ".webm" => "video/webm",
        ".mkv" => "video/x-matroska",
        ".m4a" or ".m4b" or ".aac" => "audio/mp4",
        ".mp3" => "audio/mpeg",
        ".wav" => "audio/wav",
        ".aif" or ".aiff" => "audio/aiff",
        ".caf" => "audio/x-caf",
        ".flac" => "audio/flac",
        _ => "application/octet-stream"
    };
}
