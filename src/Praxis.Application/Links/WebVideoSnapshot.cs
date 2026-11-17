using System.Text.Json.Serialization;

namespace Praxis.Application.Links;

/// <summary>
/// What Praxis keeps of an online video it did not download: where it lives, how to embed it,
/// its public metadata and its published captions with timing. Stored as a <c>.webvideo</c> file.
/// </summary>
public sealed record WebVideoSnapshot(
    [property: JsonPropertyName("praxisWebVideo")] int Version,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("embedUrl")] string EmbedUrl,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("durationSeconds")] double? DurationSeconds,
    [property: JsonPropertyName("cues")] IReadOnlyList<WebVideoCue> Cues,
    [property: JsonPropertyName("transcriptOrigin")] string? TranscriptOrigin = null,
    [property: JsonPropertyName("audioSha256")] string? AudioSha256 = null,
    [property: JsonPropertyName("frames")] IReadOnlyList<WebVideoFrame>? Frames = null,
    [property: JsonPropertyName("studyReason")] string? StudyReason = null,
    [property: JsonPropertyName("visionInputTokens")] int VisionInputTokens = 0,
    [property: JsonPropertyName("visionOutputTokens")] int VisionOutputTokens = 0)
{
    public const string Extension = ".webvideo";
    public const int CurrentVersion = 1;
}
