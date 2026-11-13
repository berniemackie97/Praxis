namespace Praxis.Application.Documents.Pdf;

/// <summary>
/// Reads the positioned text of a PDF into visual lines. Implementations never execute document
/// scripts, fetch external resources or prompt for passwords.
/// </summary>
public interface IPdfTextReader
{
    PdfTextResult Read(byte[] document, int maximumPages);
}
