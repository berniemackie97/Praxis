namespace Praxis.Storage;

public sealed record LocalMediaInspection(
    Guid SourceId, Guid ItemId, int Revision, string SourceSha256, long OriginalBytes,
    string SourceExtension, string Outcome, string? ContainerFamily,
    long? DurationMilliseconds, string? ReasonCode, string Producer);

public sealed record LocalVideoCueHit(
    Guid CaptionSourceId, Guid CaptionItemId, int CaptionRevision, string CaptionSha256,
    int CaptionOffset, int CaptionLength, int CueNumber, long StartMilliseconds,
    long EndMilliseconds, string CaptionText, string Producer,
    Guid VideoSourceId, Guid VideoItemId, int VideoRevision, string VideoSha256,
    string VideoLocator);

/// <summary>
/// Read-only evidence about captured media: verified header inspection and timecoded caption
/// cues bound to an exact video digest. Neither grants playback or decodes frames.
/// </summary>
public sealed class LocalMediaEvidence
{
    private readonly LocalSources sources;
    private readonly LocalTextSearch search;
    private readonly LocalTextDerivatives text;
    private readonly OriginalFileStore objects;

    internal LocalMediaEvidence(
        LocalSources sources, LocalTextSearch search, LocalTextDerivatives text, OriginalFileStore objects)
    {
        this.sources = sources;
        this.search = search;
        this.text = text;
        this.objects = objects;
    }

    public Task<LocalMediaInspection> InspectAsync(
        Guid sourceId, CancellationToken cancellationToken = default) =>
        InspectAsync(sourceId, expectedRevision: null, cancellationToken);

    internal async Task<LocalMediaInspection> InspectAsync(
        Guid sourceId, int? expectedRevision, CancellationToken cancellationToken)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("Select a captured media source.", nameof(sourceId));
        }

        var source = sources.CurrentFile(sourceId)
            ?? throw new InvalidOperationException("The media source is no longer active.");
        if (expectedRevision is { } revision && revision != source.Revision)
        {
            throw new InvalidOperationException("The selected media revision is no longer current.");
        }

        var fileExtension = Path.GetExtension(source.Locator).ToLowerInvariant();
        if (fileExtension is not (".mp4" or ".mov" or ".m4v" or ".webm" or ".mkv"))
        {
            throw new ArgumentException("Media inspection requires a selected video original.",
                nameof(sourceId));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        byte[] header;
        try
        {
            header = await objects.ReadVerifiedPrefixAsync(new OriginalObject(source.Digest, source.Length),
                    64 * 1024, 512L * 1024 * 1024, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Media inspection exceeded its time budget.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (sources.CurrentFile(sourceId) != source)
        {
            throw new InvalidOperationException("The selected media source changed or was revoked.");
        }

        var (container, duration) = LocalMediaHeaderParser.Read(header, fileExtension);
        return new LocalMediaInspection(
            sourceId, source.ItemId, source.Revision, source.Digest, source.Length,
            fileExtension, container is null ? "unsupported" : "header_recognized",
            container, duration, container is null ? "unsupported_signature" : null,
            LocalProcessors.MediaHeader);
    }

    public IReadOnlyList<LocalVideoCueHit> SearchCues(string query, int limit = 20)
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
        foreach (var hit in search.Query(query, 40))
        {
            var extension = Path.GetExtension(hit.SourceLocator).ToLowerInvariant();
            if (extension is not (".vtt" or ".srt"))
            {
                continue;
            }

            var derivative = text.Read(hit.ItemId, hit.Revision);
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
                var current = sources.FindLatestFile(hit.SourceLocator);
                var currentVideo = sources.FindLatestFile(video.Value.Locator);
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
                    LocalProcessors.TimedCaption, currentVideo.SourceId, currentVideo.ItemId,
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

        var video = sources.FindLatestFile(locator);
        return video?.Status == "active" &&
            video.Sha256.Equals(expectedDigest, StringComparison.OrdinalIgnoreCase)
            ? (video, locator) : null;
    }
}
