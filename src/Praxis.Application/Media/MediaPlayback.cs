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
    public const string CurrentTimebase = "media-clock-v1";
    public const string AudioSampleTimebase = "audio-sample-v1";

    public static bool UsesAudioSampleClock(string? extension) => extension?.ToLowerInvariant() is ".mp4" or ".mov" or ".m4v";

    public async Task<MediaPlaybackReference> ResolveAsync(Guid sourceId, CancellationToken cancellationToken,
        Guid? expectedItemId = null, int? expectedRevision = null, string? timebase = null, bool citedClip = false)
    {
        CurrentFileVersion current = sources.CurrentFile(sourceId) ?? throw new InvalidOperationException("The media source is no longer active.");
        if (expectedItemId is { } expected && (current.ItemId != expected || current.Revision != expectedRevision))
        {
            throw new InvalidOperationException("That cited media revision changed. Ask for a fresh clip.");
        }

        string extension = Path.GetExtension(current.Locator).ToLowerInvariant();
        if (citedClip && UsesAudioSampleClock(extension) && timebase != AudioSampleTimebase)
        {
            throw new InvalidOperationException("This older clip has no verified timebase. Ask for a fresh clip from the same recording.");
        }
        if (extension is ".vtt" or ".srt" && captions.PairedVideo(current.Locator) is { } video)
        {
            MediaPlaybackReference paired = await ResolveAsync(video.Source.SourceId, cancellationToken, video.Source.ItemId, video.Source.Revision);
            EnsureCurrent(sourceId, current);
            return paired;
        }

        if (web is not null && await web.ResolveAsync(sourceId, cancellationToken) is { } online)
        {
            EnsureCurrent(sourceId, current);
            return online;
        }

        if (!MediaFormats.IsTranscribable(extension) && !MediaFormats.IsVideo(extension))
        {
            throw new ArgumentException("That source is not playable media.", nameof(sourceId));
        }

        string path = await originals.MaterializeAsync(new OriginalObject(current.Digest, current.Length), extension, cancellationToken);
        try
        {
            EnsureCurrent(sourceId, current);
            return new MediaPlaybackReference(sourceId, Path.GetFileName(current.Locator), "file", path, null, MediaFormats.MimeType(extension),
                MediaFormats.IsVideo(extension));
        }
        catch
        {
            originals.ReleaseMaterialized(path);
            throw;
        }
    }

    private void EnsureCurrent(Guid sourceId, CurrentFileVersion expected)
    {
        if (sources.CurrentFile(sourceId) is not { } active || active.ItemId != expected.ItemId ||
            active.Revision != expected.Revision || active.Digest != expected.Digest)
        {
            throw new InvalidOperationException("That cited media revision changed. Ask for a fresh clip.");
        }
    }
}
