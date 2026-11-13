using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Praxis.Application.Documents.Html;

/// <summary>
/// Converts HTML to readable text without a DOM: scripts, styles, templates and comments are
/// dropped, block elements become line breaks, table cells become <c> | </c> separators, and
/// entities are decoded. Linear-time regular expressions with a match timeout bound hostile input.
/// </summary>
public static partial class HtmlText
{
    public static string Convert(string html)
    {
        string text = Hidden().Replace(html, " ");
        text = Comments().Replace(text, " ");
        text = Breaks().Replace(text, "\n");
        text = Cells().Replace(text, " | ");
        text = Blocks().Replace(text, "\n");
        text = Tags().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');
        StringBuilder builder = new(text.Length);
        foreach (string line in text.Split('\n'))
        {
            builder.Append(Spaces().Replace(line, " ").Trim().Trim('|').Trim()).Append('\n');
        }

        return builder.ToString();
    }

    public static string? Title(string html) =>
        TitleElement().Match(html) is { Success: true } match ? WebUtility.HtmlDecode(match.Groups["title"].Value).Trim() is { Length: > 0 } title ? title : null : null;

    [GeneratedRegex(@"<(script|style|template|noscript|head|svg)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 2_000)]
    private static partial Regex Hidden();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline, 2_000)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase, 2_000)]
    private static partial Regex Breaks();

    [GeneratedRegex(@"</t[dh]\s*>", RegexOptions.IgnoreCase, 2_000)]
    private static partial Regex Cells();

    [GeneratedRegex(@"</?(p|div|section|article|header|footer|main|aside|nav|li|ul|ol|tr|table|h[1-6]|blockquote|pre|dd|dt|dl|hr|figure|figcaption)\b[^>]*>", RegexOptions.IgnoreCase, 2_000)]
    private static partial Regex Blocks();

    [GeneratedRegex(@"<[^>]{0,4096}>", RegexOptions.None, 2_000)]
    private static partial Regex Tags();

    [GeneratedRegex(@"[ \t\f\v]+", RegexOptions.None, 2_000)]
    private static partial Regex Spaces();

    [GeneratedRegex(@"<title\b[^>]*>(?<title>.*?)</title\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 2_000)]
    private static partial Regex TitleElement();
}
