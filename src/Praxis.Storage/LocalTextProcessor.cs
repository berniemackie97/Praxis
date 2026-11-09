using System.Text;

namespace Praxis.Storage;

public sealed class LocalTextProcessor(LocalCatalog catalog)
{
    public const int MaximumTextBytes = 1024 * 1024;

    // An extension establishes a supported text decoding policy, not the file's semantics.
    internal static readonly string[] SupportedExtensions =
    [
        ".txt", ".md", ".markdown",
        ".cs", ".csx", ".fs", ".fsx",
        ".c", ".h", ".cc", ".cpp", ".cxx", ".hh", ".hpp", ".hxx",
        ".asm", ".s", ".lst"
    ];

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lease = catalog.TryClaimProcessing(LeaseDuration,
            processorKey: LocalCatalog.DefaultProcessorKey);
        if (lease is null && catalog.RequeueNewlySupportedText(SupportedExtensions))
        {
            lease = catalog.TryClaimProcessing(LeaseDuration,
                processorKey: LocalCatalog.DefaultProcessorKey);
        }

        if (lease is null)
        {
            return false;
        }

        var source = catalog.ReadLeasedTextSource(lease);
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

        var extension = Path.GetExtension(source.Locator).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension, StringComparer.Ordinal))
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
            var store = new OriginalFileStore(Path.GetDirectoryName(catalog.DatabasePath)!);
            var original = await store.ReadVerifiedBytesAsync(
                source.Original, MaximumTextBytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (HasNonTextSignature(original))
            {
                catalog.CommitTextResult(lease, null, "signature_mismatch");
                return true;
            }

            var offset = original.AsSpan().StartsWith(Encoding.UTF8.Preamble)
                ? Encoding.UTF8.Preamble.Length : 0;
            var text = StrictUtf8.GetString(original, offset, original.Length - offset);
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

    private static bool HasNonTextSignature(ReadOnlySpan<byte> input)
    {
        return input.StartsWith("%PDF-"u8) ||
               input.StartsWith([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]) ||
               input.StartsWith([0x50, 0x4b, 0x03, 0x04]) ||
               input.StartsWith([0x7f, 0x45, 0x4c, 0x46]) ||
               input.StartsWith([0x1f, 0x8b]) ||
               input.StartsWith([0xcf, 0xfa, 0xed, 0xfe]) ||
               input.StartsWith([0xfe, 0xed, 0xfa, 0xcf]) ||
               input.StartsWith([0xca, 0xfe, 0xba, 0xbe]);
    }
}
