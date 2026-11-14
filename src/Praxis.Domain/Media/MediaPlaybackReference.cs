namespace Praxis.Domain.Media;

/// <summary>
/// How the app plays a media source: a verified local file (<c>file</c>, with a MIME type for
/// players that need one) or an online video's privacy-enhanced embed (<c>web</c>).
/// </summary>
public sealed record MediaPlaybackReference(Guid SourceId, string Name, string Kind, string? Path, string? Url, string MimeType, bool IsVideo);
