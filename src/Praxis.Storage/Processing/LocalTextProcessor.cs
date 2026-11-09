using System.Text;

namespace Praxis.Storage;

/// <summary>
/// Extracts strictly decoded UTF-8 text from verified originals with a supported text
/// extension. Other inputs receive an explicit unsupported or failed outcome, never guessed text.
/// </summary>
public sealed class LocalTextProcessor(LocalCatalog catalog) : ILocalProcessingHandler
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

    public string ProcessorKey => LocalProcessors.SourceText;
    public TimeSpan LeaseDuration => TimeSpan.FromMinutes(5);
    public int MaxConcurrency => 2;

    public bool TryReopenWork() => catalog.Text.RequeueNewlySupported(SupportedExtensions);

    public async Task ProcessAsync(ProcessingLease lease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = catalog.Text.ReadLeasedSource(lease);
        if (source is null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (source.SourceKind != "file")
        {
            catalog.Text.CommitResult(lease, null, "unsupported_source");
            return;
        }

        var extension = Path.GetExtension(source.Locator).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension, StringComparer.Ordinal))
        {
            catalog.Text.CommitResult(lease, null,
                extension == ".pdf" ? "pdf_not_supported" : "unsupported_format");
            return;
        }

        if (source.Original.Length > MaximumTextBytes)
        {
            catalog.Text.CommitResult(lease, null, "text_too_large");
            return;
        }

        try
        {
            var original = await catalog.Originals.ReadVerifiedBytesAsync(
                source.Original, MaximumTextBytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (HasNonTextSignature(original))
            {
                catalog.Text.CommitResult(lease, null, "signature_mismatch");
                return;
            }

            var offset = original.AsSpan().StartsWith(Encoding.UTF8.Preamble)
                ? Encoding.UTF8.Preamble.Length : 0;
            var text = StrictUtf8.GetString(original, offset, original.Length - offset);
            if (text.Any(c => (c < 0x20 && c is not ('\n' or '\r' or '\t')) ||
                c == 0x7f))
            {
                catalog.Text.CommitResult(lease, null, "binary_content");
                return;
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
