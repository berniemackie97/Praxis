using System.Text;

namespace Praxis.Application.Documents;

/// <summary>
/// Decodes text originals. UTF-8 (with or without BOM) and UTF-16 with BOM are read
/// strictly; anything else that is not valid UTF-8 is read as Windows-1252, the encoding OFX
/// 1.x and most legacy bank exports declare. Content containing NUL is treated as binary.
/// </summary>
public static class TextDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Encoding Windows1252 = CodePagesEncodingProvider.Instance.GetEncoding(1252)
        ?? throw new InvalidOperationException("Windows-1252 decoding is unavailable.");

    /// <summary>The encoding a MIME or XML charset label names, including legacy code pages, or null.</summary>
    public static Encoding? ForCharset(string label)
    {
        string name = label.Trim().Trim('"', '\'');
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(name) ?? Encoding.GetEncoding(name);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    public static string? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Encoding.UTF8.Preamble))
        {
            return Strict(bytes[Encoding.UTF8.Preamble.Length..]);
        }

        if (bytes.StartsWith(Encoding.Unicode.Preamble))
        {
            return Clean(new UnicodeEncoding(false, false, true), bytes[2..]);
        }

        if (bytes.StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            return Clean(new UnicodeEncoding(true, false, true), bytes[2..]);
        }

        if (bytes.Contains((byte)0))
        {
            return null;
        }

        return Strict(bytes) ?? Windows1252.GetString(bytes);
    }

    private static string? Strict(ReadOnlySpan<byte> bytes) => Clean(StrictUtf8, bytes);

    private static string? Clean(Encoding encoding, ReadOnlySpan<byte> bytes)
    {
        try
        {
            string text = encoding.GetString(bytes);
            return text.Contains('\0') ? null : text;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
