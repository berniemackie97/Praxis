using System.Text;
using System.Text.RegularExpressions;

namespace Praxis.Application.Documents.Email;

/// <summary>
/// A bounded RFC 5322 / MIME parser: unfolds headers, decodes RFC 2047 encoded words, walks
/// multipart bodies to a fixed depth and part count, and decodes base64 and quoted-printable
/// transfer encodings. It never follows external references or executes content.
/// </summary>
public static partial class MimeParser
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    public static MimeMessage? Parse(byte[] bytes)
    {
        string raw = Latin1.GetString(bytes);
        (Dictionary<string, string> headers, string body) = Split(raw);
        if (headers.Count == 0)
        {
            return null;
        }

        List<MimePart> parts = [];
        Walk(headers, body, 0, parts);
        Dictionary<string, string> decoded = headers.ToDictionary(pair => pair.Key, pair => DecodeWords(pair.Value), StringComparer.OrdinalIgnoreCase);
        return new MimeMessage(decoded, parts);
    }

    private static void Walk(Dictionary<string, string> headers, string body, int depth, List<MimePart> parts)
    {
        if (depth > DocumentLimits.MaximumMimeDepth || parts.Count >= DocumentLimits.MaximumMimeParts)
        {
            return;
        }

        string contentType = headers.GetValueOrDefault("Content-Type") ?? "text/plain";
        string mediaType = contentType.Split(';')[0].Trim().ToLowerInvariant();
        if (mediaType.StartsWith("multipart/", StringComparison.Ordinal) && Parameter(contentType, "boundary") is { Length: > 0 } boundary)
        {
            foreach (string section in Sections(body, boundary))
            {
                (Dictionary<string, string> childHeaders, string childBody) = Split(section);
                Walk(childHeaders, childBody, depth + 1, parts);
            }

            return;
        }

        if (mediaType == "message/rfc822")
        {
            (Dictionary<string, string> innerHeaders, string innerBody) = Split(body);
            Walk(innerHeaders, innerBody, depth + 1, parts);
            return;
        }

        string disposition = headers.GetValueOrDefault("Content-Disposition") ?? string.Empty;
        string? fileName = Parameter(disposition, "filename") ?? Parameter(contentType, "name");
        bool attachment = disposition.StartsWith("attachment", StringComparison.OrdinalIgnoreCase) ||
                          (fileName is not null && !mediaType.StartsWith("text/", StringComparison.Ordinal));
        byte[] content = Decode(body, headers.GetValueOrDefault("Content-Transfer-Encoding"));
        parts.Add(new MimePart(mediaType, fileName is null ? null : DecodeWords(fileName), attachment, content, Parameter(contentType, "charset") ?? "utf-8"));
    }

    /// <summary>The text of a part in its declared charset, falling back to UTF-8 then Latin-1.</summary>
    public static string Text(MimePart part)
    {
        return TextDecoder.ForCharset(part.Charset) is { } encoding
            ? encoding.GetString(part.Content)
            : TextDecoder.Decode(part.Content) ?? Latin1.GetString(part.Content);
    }

    private static (Dictionary<string, string> Headers, string Body) Split(string raw)
    {
        int end = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        int separator = 4;
        int lf = raw.IndexOf("\n\n", StringComparison.Ordinal);
        if (end < 0 || (lf >= 0 && lf < end))
        {
            end = lf;
            separator = 2;
        }

        string head = end < 0 ? raw : raw[..end];
        string body = end < 0 ? string.Empty : raw[(end + separator)..];
        Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
        string? name = null;
        foreach (string line in head.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && name is not null)
            {
                headers[name] += " " + line.Trim();
                continue;
            }

            int colon = line.IndexOf(':');
            if (colon <= 0 || line[..colon].Any(char.IsWhiteSpace))
            {
                name = null;
                continue;
            }

            name = line[..colon];
            headers.TryAdd(name, line[(colon + 1)..].Trim());
        }

        return (headers, body);
    }

    private static IEnumerable<string> Sections(string body, string boundary)
    {
        string delimiter = "--" + boundary;
        int index = body.IndexOf(delimiter, StringComparison.Ordinal);
        while (index >= 0)
        {
            int start = body.IndexOf('\n', index);
            if (start < 0 || body.AsSpan(index + delimiter.Length).StartsWith("--"))
            {
                yield break;
            }

            int next = body.IndexOf("\n" + delimiter, start, StringComparison.Ordinal);
            yield return next < 0 ? body[(start + 1)..] : body[(start + 1)..next].TrimEnd('\r');
            index = next < 0 ? -1 : next + 1;
        }
    }

    private static byte[] Decode(string body, string? transferEncoding)
    {
        switch (transferEncoding?.Trim().ToLowerInvariant())
        {
            case "base64":
                try
                {
                    return Convert.FromBase64String(Base64Noise().Replace(body, string.Empty));
                }
                catch (FormatException)
                {
                    return Latin1.GetBytes(body);
                }
            case "quoted-printable":
                return QuotedPrintable(body, header: false);
            default:
                return Latin1.GetBytes(body);
        }
    }

    private static byte[] QuotedPrintable(string text, bool header)
    {
        List<byte> bytes = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '=' && i + 1 < text.Length && (text[i + 1] == '\n' || (text[i + 1] == '\r' && i + 2 < text.Length && text[i + 2] == '\n')))
            {
                i += text[i + 1] == '\r' ? 2 : 1;
            }
            else if (c == '=' && i + 2 < text.Length && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]))
            {
                bytes.Add(Convert.ToByte(text.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.Add(header && c == '_' ? (byte)' ' : (byte)c);
            }
        }

        return bytes.ToArray();
    }

    /// <summary>Decodes RFC 2047 encoded words such as <c>=?UTF-8?B?...?=</c> in a header value.</summary>
    public static string DecodeWords(string value) => EncodedWord().Replace(value, match =>
    {
        try
        {
            Encoding encoding = TextDecoder.ForCharset(match.Groups["charset"].Value) ?? Encoding.UTF8;
            byte[] bytes = match.Groups["encoding"].Value.Equals("B", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(match.Groups["text"].Value)
                : QuotedPrintable(match.Groups["text"].Value, header: true);
            return encoding.GetString(bytes);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return match.Value;
        }
    });

    private static string? Parameter(string header, string name)
    {
        Match match = HeaderParameter().Match(header);
        while (match.Success)
        {
            if (match.Groups["name"].Value.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return match.Groups["value"].Value.Trim('"');
            }

            match = match.NextMatch();
        }

        return null;
    }

    [GeneratedRegex(@"=\?(?<charset>[^?]+)\?(?<encoding>[BbQq])\?(?<text>[^?]*)\?=", RegexOptions.None, 1_000)]
    private static partial Regex EncodedWord();

    [GeneratedRegex(@";\s*(?<name>[\w\-*]+)\s*=\s*(?<value>""[^""]*""|[^;]+)", RegexOptions.None, 1_000)]
    private static partial Regex HeaderParameter();

    [GeneratedRegex(@"[^A-Za-z0-9+/=]", RegexOptions.None, 1_000)]
    private static partial Regex Base64Noise();
}
