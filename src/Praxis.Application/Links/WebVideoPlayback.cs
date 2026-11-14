using Praxis.Application.Media;
using Praxis.Application.Originals;
using Praxis.Application.Sources;
using Praxis.Domain.Media;
using Praxis.Domain.Originals;

namespace Praxis.Application.Links;

/// <summary>Plays a saved online video (<c>.webvideo</c> snapshot) through the embed URL recorded when it was saved.</summary>
public sealed class WebVideoPlayback(ISourceRepository sources, IOriginalStore originals) : IWebVideoResolver
{
    private const int MaximumBytes = 8 * 1024 * 1024;

    public async Task<MediaPlaybackReference?> ResolveAsync(Guid sourceId, CancellationToken cancellationToken)
    {
        if (sources.CurrentFile(sourceId) is not { } current || !current.Locator.EndsWith(WebVideoSnapshot.Extension, StringComparison.OrdinalIgnoreCase) ||
            current.Length > MaximumBytes)
        {
            return null;
        }

        byte[] bytes = await originals.ReadVerifiedBytesAsync(new OriginalObject(current.Digest, current.Length), MaximumBytes, cancellationToken);
        return WebVideoReader.Parse(bytes) is { } snapshot
            ? new MediaPlaybackReference(sourceId, snapshot.Title, "web", null, snapshot.EmbedUrl, "text/html", IsVideo: true)
            : null;
    }
}
