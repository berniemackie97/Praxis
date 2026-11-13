namespace Praxis.Application.Documents.Html;

/// <summary>Saved web pages and HTML exports, read as text with their title.</summary>
public sealed class HtmlDocumentReader : IDocumentReader
{
    public IReadOnlyCollection<string> Extensions { get; } = [".html", ".htm", ".xhtml", ".mhtml"];

    public bool CanRead(DocumentInput input)
    {
        if (BinarySignature.Matches(input.Bytes) || BinarySignature.BinaryExtensions.Contains(input.Extension))
        {
            return false;
        }

        if (Extensions.Contains(input.Extension))
        {
            return true;
        }

        string head = TextDecoder.Decode(input.Bytes.AsSpan(0, Math.Min(input.Bytes.Length, 1_024)))?.TrimStart() ?? string.Empty;
        return head.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || head.StartsWith("<html", StringComparison.OrdinalIgnoreCase);
    }

    public Task<ReadOutcome> ReadAsync(DocumentInput input, CancellationToken cancellationToken)
    {
        string? html = TextDecoder.Decode(input.Bytes);
        if (html is null)
        {
            return Task.FromResult(ReadOutcome.Unreadable("binary_content"));
        }

        try
        {
            return Task.FromResult(ReadOutcome.Read(new ReadDocument(DocumentFormats.Html, HtmlText.Convert(html), Title: HtmlText.Title(html))));
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            return Task.FromResult(ReadOutcome.Unreadable("html_too_complex"));
        }
    }
}
