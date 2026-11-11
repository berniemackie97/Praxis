using Praxis.Domain.Originals;

namespace Praxis.Application.Originals;

/// <summary>
/// Immutable, content-addressed originals and their catalog records. A capture commits the
/// source/item revision, its idempotent request receipt and its first processing job atomically.
/// </summary>
public interface IOriginalStore
{
    /// <summary>
    /// Captures a file's bytes under <paramref name="requestKey"/>. Repeating a request returns its
    /// receipt after re-verifying the stored object; the capture must stay inside its declared scope.
    /// </summary>
    Task<CapturedOriginal> CaptureFileAsync(
        string sourcePath, Stream content, long maxBytes, string requestKey, CaptureRequestContext capture,
        CancellationToken cancellationToken, string? expectedDigest = null, long? expectedLength = null);

    /// <summary>Reads a whole original after verifying its recorded length and SHA-256.</summary>
    Task<byte[]> ReadVerifiedBytesAsync(OriginalObject original, int maxBytes, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the first <paramref name="prefixBytes"/> of an original no larger than
    /// <paramref name="maxBytes"/>, after verifying the whole object against its digest.
    /// </summary>
    Task<byte[]> ReadVerifiedPrefixAsync(OriginalObject original, int prefixBytes, long maxBytes, CancellationToken cancellationToken);

    /// <summary>Verifies the latest stored revision of an item against its recorded digest.</summary>
    Task VerifyAsync(Guid itemId, CancellationToken cancellationToken);

    /// <summary>Copies a verified original revision to an owner-chosen destination outside the catalog.</summary>
    Task<OriginalExport> ExportAsync(Guid itemId, int revision, string destinationPath, CancellationToken cancellationToken);
}
