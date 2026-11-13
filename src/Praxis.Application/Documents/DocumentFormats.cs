namespace Praxis.Application.Documents;

/// <summary>Normalized format families a reader produces. Formats choose rendering, never the intake path.</summary>
public static class DocumentFormats
{
    public const string Text = "text";
    public const string Markdown = "markdown";
    public const string Subtitles = "subtitles";
    public const string Code = "code";
    public const string Pdf = "pdf";
    public const string WordProcessing = "docx";
    public const string Spreadsheet = "xlsx";
    public const string Delimited = "delimited";
    public const string FinancialInterchange = "financial_interchange";
    public const string Email = "email";
    public const string Html = "html";
    public const string Image = "image";
}
