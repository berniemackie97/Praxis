namespace Praxis.Storage;

public sealed record LocalVideoCueHit(
    Guid CaptionSourceId, Guid CaptionItemId, int CaptionRevision, string CaptionSha256,
    int CaptionOffset, int CaptionLength, int CueNumber, long StartMilliseconds,
    long EndMilliseconds, string CaptionText, string Producer,
    Guid VideoSourceId, Guid VideoItemId, int VideoRevision, string VideoSha256,
    string VideoLocator);

public sealed partial class LocalCatalog
{
    public IReadOnlyList<LocalVideoCueHit> SearchVideoCues(string query, int limit = 20)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        // Existing FTS is a bounded candidate selector. The matching cue itself must
        // also contain the requested terms; a document-level match alone is insufficient.
        var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0 || terms.Length > 12)
        {
            throw new ArgumentException("Use at most twelve search terms.", nameof(query));
        }

        var results = new List<LocalVideoCueHit>();
        foreach (var hit in SearchText(query, 40))
        {
            var extension = Path.GetExtension(hit.SourceLocator).ToLowerInvariant();
            if (extension is not (".vtt" or ".srt"))
            {
                continue;
            }

            var derivative = ReadTextDerivative(hit.ItemId, hit.Revision);
            if (derivative?.Content is not { } content ||
                derivative.SourceSha256 != hit.OriginalSha256 ||
                derivative.SourceId != hit.SourceId)
            {
                continue;
            }

            var video = FindPairedVideo(hit.SourceLocator);
            if (video is null)
            {
                continue;
            }

            IReadOnlyList<TimedCue> cues;
            try
            {
                cues = LocalTimedCaptionParser.Parse(content, extension);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            foreach (var cue in cues)
            {
                if (!terms.All(term => cue.Text.Contains(term, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                // Recheck current attribution after reading the derivative and pairing.
                var current = FindLatestFile(hit.SourceLocator);
                var currentVideo = FindLatestFile(video.Value.Locator);
                if (current is null || current.SourceId != hit.SourceId ||
                    current.Revision != hit.Revision || current.Sha256 != hit.OriginalSha256 ||
                    current.Status != "active" ||
                    currentVideo != video.Value.Source || currentVideo.Status != "active")
                {
                    break;
                }

                results.Add(new LocalVideoCueHit(
                    hit.SourceId, hit.ItemId, hit.Revision, hit.OriginalSha256,
                    cue.Offset, cue.Length, cue.Number,
                    cue.StartMilliseconds, cue.EndMilliseconds, cue.Text,
                    "timed-caption-v1", currentVideo.SourceId, currentVideo.ItemId,
                    currentVideo.Revision, currentVideo.Sha256, video.Value.Locator));
                if (results.Count == limit)
                {
                    return results;
                }
            }
        }

        return results;
    }

    private (LocalFileVersion Source, string Locator)? FindPairedVideo(string captionPath)
    {
        // The sidecar must name the exact preserved video's SHA-256, not only its
        // filename; a replacement video cannot inherit unrelated old timestamps.
        var stem = Path.Combine(Path.GetDirectoryName(captionPath)!,
            Path.GetFileNameWithoutExtension(captionPath));
        var lastDot = stem.LastIndexOf('.');
        if (lastDot < 0 || stem.Length - lastDot - 1 != 64)
        {
            return null;
        }

        var expectedDigest = stem[(lastDot + 1)..];
        var locator = stem[..lastDot];
        if (!expectedDigest.All(Uri.IsHexDigit) ||
            Path.GetExtension(locator).ToLowerInvariant() is not
                (".mp4" or ".mov" or ".m4v" or ".webm" or ".mkv"))
        {
            return null;
        }

        var video = FindLatestFile(locator);
        return video?.Status == "active" &&
            video.Sha256.Equals(expectedDigest, StringComparison.OrdinalIgnoreCase)
            ? (video, locator) : null;
    }
}
