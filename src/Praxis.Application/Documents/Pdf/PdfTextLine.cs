namespace Praxis.Application.Documents.Pdf;

/// <summary>One visual line of a PDF page, words in reading order, with its 1-based page and line.</summary>
public sealed record PdfTextLine(int Page, int Line, string Text);
