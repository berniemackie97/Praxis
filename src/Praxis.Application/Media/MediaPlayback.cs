using Praxis.Application.Originals;
using Praxis.Application.Sources;
using Praxis.Domain.Media;
using Praxis.Domain.Originals;
using Praxis.Domain.Sources;

namespace Praxis.Application.Media;

/// <summary>
/// Resolves what the app plays for a cited media source: the preserved original of the current
/// revision (verified, linked under its container's extension), the video a caption file is bound
/// to, or a saved online video's embed.
/// </summary>
public sealed class MediaPlayback(ISourceRepository sources, IOriginalStore originals, MediaEvidence captions, IWebVideoResolver? web = null)
{
    public async Task<MediaPlaybackReference> ResolveAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        CurrentFileVersion current = sources.CurrentFile(sourceId) ?? throw new InvalidOperationException("The media source is no longer active.");
        string extension = Path.GetExtension(current.Locator).ToLowerInvariant();
        if (extension is ".vtt" or ".srt" && captions.PairedVideo(current.Locator) is { } video)
        {
            return await ResolveAsync(video.Source.SourceId, cancellationToken);
        }

        if (web?.Resolve(sourceId) is { } online)
        {
            return online;
        }

        if (!MediaFormats.IsTranscribable(extension) && !MediaFormats.IsVideo(extension))
        {
            throw new ArgumentException("That source is not playable media.", nameof(sourceId));
        }

        string path = await originals.MaterializeAsync(new OriginalObject(current.Digest, current.Length), extension, cancellationToken);
        return new MediaPlaybackReference(sourceId, Path.GetFileName(current.Locator), "file", path, null, MediaFormats.MimeType(extension),
            MediaFormats.IsVideo(extension));
    }
}
