using System.Text;
using System.Text.RegularExpressions;

namespace Praxis.Application.Links;

/// <summary>
/// The URL inside a saved link file (<c>.webloc</c> XML or binary property list, <c>.url</c>
/// shortcut) and the text form the read stage stores for it. Only <c>http</c> and <c>https</c> URLs
/// without credentials are links Praxis will ever fetch.
/// </summary>
public static partial class SavedLink
{
    public const int MaximumUrlLength = 2_048;
    private const string Prefix = "Link: ";

    public static Uri? FromFile(string extension, byte[] bytes)
    {
        string text = Encoding.UTF8.GetString(bytes.AsSpan(0, Math.Min(bytes.Length, 64 * 1024)));
        string? candidate = extension switch
        {
            ".url" => text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))?[4..],
            ".webloc" when text.StartsWith("bplist00", StringComparison.Ordinal) => Url().Match(Encoding.Latin1.GetString(bytes)).Value,
            ".webloc" => Regex.Match(text, @"<key>URL</key>\s*<string>([^<]+)</string>", RegexOptions.None, TimeSpan.FromSeconds(1)).Groups[1].Value,
            _ => null
        };
        return Parse(System.Net.WebUtility.HtmlDecode(candidate ?? string.Empty).Trim());
    }

    /// <summary>A fetchable absolute http(s) URL, or null.</summary>
    public static Uri? Parse(string? value) =>
        value is { Length: > 0 and <= MaximumUrlLength } && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) && uri.Host.Length > 0
            ? uri
            : null;

    /// <summary>The read-stage text for a saved link: the URL first, then the file's name.</summary>
    public static string ToText(Uri url, string name) => $"{Prefix}{url.AbsoluteUri}\nTitle: {name}\n";

    /// <summary>The URL from read-stage text produced by <see cref="ToText"/>.</summary>
    public static Uri? FromText(string text) =>
        text.StartsWith(Prefix, StringComparison.Ordinal) ? Parse(text[Prefix.Length..].Split('\n')[0].Trim()) : null;

    [GeneratedRegex(@"https?://[\x21-\x7e]{1,2040}", RegexOptions.None, 1_000)]
    private static partial Regex Url();
}
