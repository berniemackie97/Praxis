using System.Text;
using Praxis.Application.Documents.Ocr;

namespace Praxis.Application.Documents.Pdf;

/// <summary>
/// PDFs: the text layer as visual lines per page, pages separated by form feeds. Documents with
/// no usable text layer (scans, photographed receipts) are recognized with on-device OCR, up to a
/// page bound. Encrypted and malformed files close with their reason.
/// </summary>
public sealed class PdfDocumentReader(IPdfTextReader pdf, IOcrEngine ocr) : IDocumentReader
{
    /// <summary>Fewer recognized characters per page than this means the page is effectively an image.</summary>
    private const int MinimumCharactersPerPage = 24;

    public IReadOnlyCollection<string> Extensions { get; } = [".pdf"];

    public bool CanRead(DocumentInput input) => input.StartsWith("%PDF-"u8);

    public async Task<ReadOutcome> ReadAsync(DocumentInput input, CancellationToken cancellationToken)
    {
        PdfTextResult read = pdf.Read(input.Bytes, DocumentLimits.MaximumPdfPages);
        if (read.Lines is { } lines)
        {
            int pages = lines.Max(line => line.Page);
            if (lines.Sum(line => line.Text.Length) >= MinimumCharactersPerPage * pages)
            {
                return ReadOutcome.Read(new ReadDocument(DocumentFormats.Pdf, Render(lines), Pages: pages));
            }
        }
        else if (read.ReasonCode is not ("pdf_no_text" or null))
        {
            return ReadOutcome.Unreadable(read.ReasonCode);
        }

        if (!ocr.IsAvailable)
        {
            return read.Lines is { } partial
                ? ReadOutcome.Read(new ReadDocument(DocumentFormats.Pdf, Render(partial), Pages: partial.Max(line => line.Page)))
                : throw new Processing.ProcessorUnavailableException("ocr_unavailable");
        }

        OcrResult recognized = await ocr.RecognizeAsync(input.Bytes, OcrSource.Pdf, DocumentLimits.MaximumOcrPages, cancellationToken);
        if (recognized.Pages is not { Count: > 0 } ocrPages)
        {
            return ReadOutcome.Unreadable(recognized.ReasonCode ?? "pdf_no_text");
        }

        return ReadOutcome.Read(new ReadDocument(DocumentFormats.Pdf, string.Join($"\n{ReadDocument.PageBreak}\n", ocrPages), Pages: ocrPages.Count));
    }

    public static string Render(IEnumerable<PdfTextLine> lines)
    {
        StringBuilder builder = new();
        int page = 0;
        foreach (PdfTextLine line in lines)
        {
            if (line.Page != page)
            {
                if (page != 0)
                {
                    builder.Append(ReadDocument.PageBreak).Append('\n');
                }

                page = line.Page;
            }

            builder.Append(line.Text).Append('\n');
        }

        return builder.ToString();
    }
}
