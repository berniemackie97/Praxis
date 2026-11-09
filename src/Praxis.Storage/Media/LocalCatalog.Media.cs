namespace Praxis.Storage;

public sealed record LocalMediaInspection(
    Guid SourceId, Guid ItemId, int Revision, string SourceSha256, long OriginalBytes,
    string SourceExtension, string Outcome, string? ContainerFamily,
    long? DurationMilliseconds, string? ReasonCode, string Producer);

public sealed partial class LocalCatalog
{
    public Task<LocalMediaInspection> InspectMediaAsync(
        Guid sourceId, CancellationToken cancellationToken = default) =>
        InspectMediaAsync(sourceId, expectedRevision: null, cancellationToken);

    internal async Task<LocalMediaInspection> InspectMediaAsync(
        Guid sourceId, int? expectedRevision, CancellationToken cancellationToken)
    {
        if (sourceId == Guid.Empty)
        {
            throw new ArgumentException("Select a captured media source.", nameof(sourceId));
        }

        (Guid ItemId, int Revision, string Digest, long Length, string Locator) source;
        lock (connection)
        {
            source = CurrentFileSource(sourceId)
                ?? throw new InvalidOperationException("The media source is no longer active.");
            if (expectedRevision is { } revision && revision != source.Revision)
            {
                throw new InvalidOperationException("The selected media revision is no longer current.");
            }
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
            header = await new OriginalFileStore(Path.GetDirectoryName(DatabasePath)!)
                .ReadVerifiedPrefixAsync(new OriginalObject(source.Digest, source.Length),
                    64 * 1024, 512L * 1024 * 1024, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Media inspection exceeded its time budget.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (connection)
        {
            if (CurrentFileSource(sourceId) != source)
            {
                throw new InvalidOperationException("The selected media source changed or was revoked.");
            }
        }

        var (container, duration) = LocalMediaHeaderParser.Read(header, fileExtension);
        return new LocalMediaInspection(
            sourceId, source.ItemId, source.Revision, source.Digest, source.Length,
            fileExtension, container is null ? "unsupported" : "header_recognized",
            container, duration, container is null ? "unsupported_signature" : null,
            "media-header-v1");
    }
}
