using Praxis.Application.Processing;

namespace Praxis.Application.Documents.Ocr;

/// <summary>
/// Photos, screenshots and scans, read with on-device OCR. When the OCR engine is unavailable the
/// read is deferred rather than closed, so the image is read as soon as recognition exists.
/// </summary>
public sealed class ImageDocumentReader(IOcrEngine ocr) : IDocumentReader
{
    public IReadOnlyCollection<string> Extensions { get; } = [".png", ".jpg", ".jpeg", ".heic", ".heif", ".tif", ".tiff", ".gif", ".bmp", ".webp"];

    public bool CanRead(DocumentInput input) =>
        input.StartsWith([0x89, 0x50, 0x4e, 0x47]) || input.StartsWith([0xff, 0xd8, 0xff]) || input.StartsWith("GIF8"u8) ||
        input.StartsWith([0x49, 0x49, 0x2a, 0x00]) || input.StartsWith([0x4d, 0x4d, 0x00, 0x2a]) || input.StartsWith("BM"u8) ||
        (input.Bytes.Length >= 12 && input.Bytes.AsSpan(8, 4).SequenceEqual("WEBP"u8)) ||
        (input.Bytes.Length >= 12 && input.Bytes.AsSpan(4, 4).SequenceEqual("ftyp"u8) && input.Extension is ".heic" or ".heif");

    public async Task<ReadOutcome> ReadAsync(DocumentInput input, CancellationToken cancellationToken)
    {
        if (!ocr.IsAvailable)
        {
            throw new ProcessorUnavailableException("ocr_unavailable");
        }

        OcrResult result = await ocr.RecognizeAsync(input.Bytes, OcrSource.Image, 1, cancellationToken);
        return result.Pages is { } pages
            ? ReadOutcome.Read(new ReadDocument(DocumentFormats.Image, string.Join('\n', pages), Pages: 1))
            : ReadOutcome.Unreadable(result.ReasonCode ?? "ocr_failed");
    }
}
