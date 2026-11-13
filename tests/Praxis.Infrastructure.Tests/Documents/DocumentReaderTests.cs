using System.Text;
using Praxis.Application.Documents;
using Praxis.Application.Documents.Ocr;
using Praxis.Application.Documents.Pdf;
using Praxis.Application.Processing;
using Praxis.Infrastructure.Composition;
using Xunit;

namespace Praxis.Infrastructure.Tests.Documents;

/// <summary>Every format is read by code into normalized text, structure where useful, and bounded reasons otherwise (ADR 0044).</summary>
public sealed class DocumentReaderTests
{
    private static readonly DocumentReaders Readers = CatalogProcessing.Readers(new FakeOcr(null));

    [Fact]
    public async Task WordDocumentsKeepParagraphsTablesAndTitle()
    {
        ReadDocument document = await Read("quote.docx", OfficeFixtures.Docx("Kitchen quote", "Ortega Renovations", "Due December 1, 2026"));
        Assert.Equal(DocumentFormats.WordProcessing, document.Format);
        Assert.Equal("Kitchen quote", document.Title);
        Assert.Contains("Ortega Renovations\nDue December 1, 2026", document.Text, StringComparison.Ordinal);
        Assert.Contains("Item | Cost\nCabinets | 3,480.00", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpreadsheetsRenderSheetsAndKeepRowsForCodeSideMapping()
    {
        ReadDocument document = await Read("budget.xlsx", OfficeFixtures.Xlsx());
        Assert.Equal(DocumentFormats.Spreadsheet, document.Format);
        Assert.Contains("Sheet: Budget\nDate | Description | Amount\n2026-10-02 | Market Fresh | -84.12", document.Text, StringComparison.Ordinal);
        Assert.Equal(["2026-10-02", "Market Fresh", "-84.12"], document.Rows![1].Fields);
    }

    [Fact]
    public async Task DecompressionBombsAndForgedArchivesAreRefused()
    {
        Assert.Equal("archive_too_large", await Reason("bomb.docx", OfficeFixtures.Bomb()));
        Assert.Equal("archive_malformed", await Reason("forged.docx", [0x50, 0x4b, 0x03, 0x04, 0x41]));
    }

    [Fact]
    public async Task EmailKeepsHeadersBestBodyAndAttachmentInventory()
    {
        const string message = """
            From: Priya <priya@example.com>
            To: owner@example.com
            Subject: =?UTF-8?B?TGlzYm9uIHRyaXAg4pyI?=
            Date: Thu, 12 Nov 2026 09:30:00 -0800
            MIME-Version: 1.0
            Content-Type: multipart/mixed; boundary="outer"

            --outer
            Content-Type: multipart/alternative; boundary="inner"

            --inner
            Content-Type: text/plain; charset=utf-8
            Content-Transfer-Encoding: quoted-printable

            Flights are booked for March 3, 2026. Total =E2=82=AC412.50.=
             See you there.
            --inner
            Content-Type: text/html; charset=utf-8

            <p>Flights are booked</p>
            --inner--
            --outer
            Content-Type: application/pdf; name="itinerary.pdf"
            Content-Disposition: attachment; filename="itinerary.pdf"
            Content-Transfer-Encoding: base64

            JVBERi0xLjcK
            --outer--
            """;
        ReadDocument document = await Read("trip.eml", Encoding.UTF8.GetBytes(message.Replace("\n", "\r\n", StringComparison.Ordinal)));
        Assert.Equal(DocumentFormats.Email, document.Format);
        Assert.Equal("Lisbon trip ✈", document.Title);
        Assert.Contains("From: Priya <priya@example.com>", document.Text, StringComparison.Ordinal);
        Assert.Contains("Flights are booked for March 3, 2026. Total €412.50. See you there.", document.Text, StringComparison.Ordinal);
        Assert.Contains("- itinerary.pdf (application/pdf, 9 bytes)", document.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("<p>", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HtmlDropsScriptsAndKeepsTablesAndTitle()
    {
        ReadDocument document = await Read("page.html", Encoding.UTF8.GetBytes(
            "<html><head><title>Lease &amp; terms</title><script>steal()</script></head><body><h1>Lease</h1><p>Rent is <b>$1,800</b>.</p>" +
            "<table><tr><td>Deposit</td><td>$1,800</td></tr></table></body></html>"));
        Assert.Equal("Lease & terms", document.Title);
        Assert.DoesNotContain("steal", document.Text, StringComparison.Ordinal);
        Assert.Contains("Rent is $1,800.", document.Text, StringComparison.Ordinal);
        Assert.Contains("Deposit | $1,800", document.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TextIsSniffedByContentButBinaryExtensionsAreNeverDecodedAsText()
    {
        ReadDocument extensionless = await Read("README", "Plain notes without an extension."u8.ToArray());
        Assert.Equal(DocumentFormats.Text, extensionless.Format);
        Assert.Equal("unsupported_format", await Reason("clip.mp4", "authored text pretending to be media"u8.ToArray()));
        Assert.Equal("content_unrecognized", await Reason("report.pdf", "not really a pdf"u8.ToArray()));
    }

    [Fact]
    public async Task WrittenTextIsKeptVerbatimWhileDerivedTextIsNormalized()
    {
        ReadDocument markdown = await Read("note.md", "# Title  \n\n\n\nLine with break  \n"u8.ToArray());
        Assert.Equal("# Title  \n\n\n\nLine with break  \n", markdown.Text);
        ReadDocument latin = await Read("legacy.txt", [0x43, 0x61, 0x66, 0xe9]);
        Assert.Equal("Café", latin.Text);
    }

    [Fact]
    public async Task ScannedPdfsAreRecognizedOnDeviceAndImagesWaitForOcr()
    {
        DocumentReaders withOcr = new([new PdfDocumentReader(new NoTextPdf(), new FakeOcr(["Invoice 2026-118", "Total due $3,480.00"]))]);
        ReadDocument scanned = (await withOcr.ReadAsync(new DocumentInput("scan.pdf", "%PDF-1.7"u8.ToArray(), "src"), CancellationToken.None)).Document!;
        Assert.Equal(2, scanned.Pages);
        Assert.Equal("Invoice 2026-118\n\f\nTotal due $3,480.00", scanned.Text);

        DocumentReaders withoutOcr = new([new ImageDocumentReader(new FakeOcr(null))]);
        await Assert.ThrowsAsync<ProcessorUnavailableException>(() =>
            withoutOcr.ReadAsync(new DocumentInput("photo.png", [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a], "src"), CancellationToken.None));
    }

    [Fact]
    public void TextIsBoundedOnACharacterBoundary()
    {
        (string text, bool truncated) = TextShape.Bound("ab€cd", 4);
        Assert.True(truncated);
        Assert.Equal("ab", text);
    }

    private static async Task<ReadDocument> Read(string name, byte[] bytes) =>
        (await Readers.ReadAsync(new DocumentInput(name, bytes, "src-test"), CancellationToken.None)).Document
        ?? throw new InvalidOperationException("The document was not read.");

    private static async Task<string?> Reason(string name, byte[] bytes) =>
        (await Readers.ReadAsync(new DocumentInput(name, bytes, "src-test"), CancellationToken.None)).ReasonCode;

    private sealed class FakeOcr(IReadOnlyList<string>? pages) : IOcrEngine
    {
        public bool IsAvailable => pages is not null;

        public Task<OcrResult> RecognizeAsync(byte[] content, OcrSource source, int maximumPages, CancellationToken cancellationToken) =>
            Task.FromResult(OcrResult.Recognized(pages!));
    }

    private sealed class NoTextPdf : IPdfTextReader
    {
        public PdfTextResult Read(byte[] document, int maximumPages) => PdfTextResult.Failed("pdf_no_text");
    }
}
