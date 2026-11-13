namespace Praxis.Domain.Finance;

/// <summary>Recognized financial document formats. Recognition is by content, not file name.</summary>
public static class FinancialFormats
{
    public const string Ofx = "ofx";
    public const string Qif = "qif";
    public const string Delimited = "delimited";
    public const string PdfStatement = "pdf_statement";

    public static readonly IReadOnlyList<string> All = [Ofx, Qif, Delimited, PdfStatement];

    /// <summary>
    /// File extensions whose content is inspected. Other files are never read by the financial
    /// processor, so photos, code and notes are not scanned for account data.
    /// </summary>
    public static readonly IReadOnlyList<string> CandidateExtensions =
        [".ofx", ".qfx", ".qbo", ".qif", ".csv", ".tsv", ".pdf"];
}
