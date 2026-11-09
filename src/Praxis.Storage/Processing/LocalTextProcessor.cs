using System.Text;

namespace Praxis.Storage;

public sealed class LocalTextProcessor(LocalCatalog catalog)
{
    public const int MaximumTextBytes = 1024 * 1024;

    // An extension establishes a supported text decoding policy, not the file's semantics.
    internal static readonly string[] SupportedExtensions =
    [
        ".txt", ".md", ".markdown", ".vtt", ".srt",
        ".cs", ".csx", ".fs", ".fsx",
        ".c", ".h", ".cc", ".cpp", ".cxx", ".hh", ".hpp", ".hxx",
        ".asm", ".s", ".lst"
    ];

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(5);
    private bool legacyScanComplete;

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lease = catalog.Processing.TryClaim(LeaseDuration,
            processorKey: LocalProcessors.SourceText);
        if (lease is null && !legacyScanComplete)
        {
            if (catalog.Text.RequeueNewlySupported(SupportedExtensions))
            {
                lease = catalog.Processing.TryClaim(LeaseDuration,
                    processorKey: LocalProcessors.SourceText);
            }
            else
            {
                legacyScanComplete = true;
            }
        }

        if (lease is null)
        {
            return false;
        }

        var source = catalog.Text.ReadLeasedSource(lease);
        if (source is null)
        {
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (source.SourceKind != "file")
        {
            catalog.Text.CommitResult(lease, null, "unsupported_source");
            return true;
        }

        var extension = Path.GetExtension(source.Locator).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension, StringComparer.Ordinal))
        {
            catalog.Text.CommitResult(lease, null,
                extension == ".pdf" ? "pdf_not_supported" : "unsupported_format");
            return true;
        }

        if (source.Original.Length > MaximumTextBytes)
        {
            catalog.Text.CommitResult(lease, null, "text_too_large");
            return true;
        }

        try
        {
            var original = await catalog.Originals.ReadVerifiedBytesAsync(
                source.Original, MaximumTextBytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (HasNonTextSignature(original))
            {
                catalog.Text.CommitResult(lease, null, "signature_mismatch");
                return true;
            }

            var offset = original.AsSpan().StartsWith(Encoding.UTF8.Preamble)
                ? Encoding.UTF8.Preamble.Length : 0;
            var text = StrictUtf8.GetString(original, offset, original.Length - offset);
            if (text.Any(c => (c < 0x20 && c is not ('\n' or '\r' or '\t')) ||
                c == 0x7f))
            {
                catalog.Text.CommitResult(lease, null, "binary_content");
                return true;
            }

            catalog.Text.CommitResult(lease, text, null);
        }
        catch (DecoderFallbackException)
        {
            catalog.Processing.Finish(lease, ProcessingCompletion.Failed, "invalid_utf8");
        }
        catch (InvalidDataException)
        {
            catalog.Processing.Finish(lease, ProcessingCompletion.Failed, "corrupt_original");
        }
        catch (EndOfStreamException)
        {
            catalog.Processing.Finish(lease, ProcessingCompletion.Failed, "corrupt_original");
        }
        catch (IOException)
        {
            catalog.Processing.Finish(lease, ProcessingCompletion.Failed, "source_io");
        }
        catch (UnauthorizedAccessException)
        {
            catalog.Processing.Finish(lease, ProcessingCompletion.Failed, "source_access");
        }

        return true;
    }

    private static bool HasNonTextSignature(ReadOnlySpan<byte> input)
    {
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a];
        ReadOnlySpan<byte> zip = [0x50, 0x4b, 0x03, 0x04];
        ReadOnlySpan<byte> elf = [0x7f, 0x45, 0x4c, 0x46];
        ReadOnlySpan<byte> gzip = [0x1f, 0x8b];
        ReadOnlySpan<byte> machLittleEndian = [0xcf, 0xfa, 0xed, 0xfe];
        ReadOnlySpan<byte> machBigEndian = [0xfe, 0xed, 0xfa, 0xcf];
        ReadOnlySpan<byte> machUniversal = [0xca, 0xfe, 0xba, 0xbe];

        return input.StartsWith("%PDF-"u8) ||
               input.StartsWith(png) ||
               input.StartsWith(zip) ||
               input.StartsWith(elf) ||
               input.StartsWith(gzip) ||
               input.StartsWith(machLittleEndian) ||
               input.StartsWith(machBigEndian) ||
               input.StartsWith(machUniversal);
    }
}
