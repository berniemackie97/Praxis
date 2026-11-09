using System.Text;

namespace Praxis.Storage;

public sealed class LocalTextProcessor(LocalCatalog catalog)
{
    public const int MaximumTextBytes = 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProcessingLease? lease = catalog.TryClaimProcessing(LeaseDuration,
            processorKey: LocalCatalog.DefaultProcessorKey);
        if (lease is null)
        {
            return false;
        }

        LeasedTextSource? source = catalog.ReadLeasedTextSource(lease);
        if (source is null)
        {
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (source.SourceKind != "file")
        {
            catalog.CommitTextResult(lease, null, "unsupported_source");
            return true;
        }

        string extension = Path.GetExtension(source.Locator).ToLowerInvariant();
        if (extension is not (".txt" or ".md" or ".markdown"))
        {
            catalog.CommitTextResult(lease, null,
                extension == ".pdf" ? "pdf_not_supported" : "unsupported_format");
            return true;
        }

        if (source.Original.Length > MaximumTextBytes)
        {
            catalog.CommitTextResult(lease, null, "text_too_large");
            return true;
        }

        try
        {
            OriginalFileStore store = new OriginalFileStore(Path.GetDirectoryName(catalog.DatabasePath)!);
            byte[] original = await store.ReadVerifiedBytesAsync(
                source.Original, MaximumTextBytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (original.AsSpan().StartsWith("%PDF-"u8))
            {
                catalog.CommitTextResult(lease, null, "signature_mismatch");
                return true;
            }

            int offset = original.AsSpan().StartsWith(Encoding.UTF8.Preamble)
                ? Encoding.UTF8.Preamble.Length : 0;
            string text = StrictUtf8.GetString(original, offset, original.Length - offset);
            if (text.Any(c => (c < 0x20 && c is not ('\n' or '\r' or '\t')) ||
                c == 0x7f))
            {
                catalog.CommitTextResult(lease, null, "binary_content");
                return true;
            }

            catalog.CommitTextResult(lease, text, null);
        }
        catch (DecoderFallbackException)
        {
            catalog.FinishProcessing(lease, ProcessingCompletion.Failed, "invalid_utf8");
        }
        catch (InvalidDataException)
        {
            catalog.FinishProcessing(lease, ProcessingCompletion.Failed, "corrupt_original");
        }
        catch (EndOfStreamException)
        {
            catalog.FinishProcessing(lease, ProcessingCompletion.Failed, "corrupt_original");
        }
        catch (IOException)
        {
            catalog.FinishProcessing(lease, ProcessingCompletion.Failed, "source_io");
        }
        catch (UnauthorizedAccessException)
        {
            catalog.FinishProcessing(lease, ProcessingCompletion.Failed, "source_access");
        }

        return true;
    }
}
