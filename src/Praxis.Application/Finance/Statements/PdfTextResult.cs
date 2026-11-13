namespace Praxis.Application.Finance.Statements;

/// <summary>The text lines of a PDF, or the bounded reason it could not be read (encrypted, malformed, image-only).</summary>
public sealed record PdfTextResult(IReadOnlyList<PdfTextLine>? Lines, string? ReasonCode)
{
    public static PdfTextResult Read(IReadOnlyList<PdfTextLine> lines) => new(lines, null);

    public static PdfTextResult Failed(string reasonCode) => new(null, reasonCode);
}
