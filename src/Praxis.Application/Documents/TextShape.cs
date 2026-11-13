using System.Text;

namespace Praxis.Application.Documents;

/// <summary>Whitespace normalization and byte-bounded truncation shared by all readers.</summary>
public static class TextShape
{
    /// <summary>Normalizes line endings, trims trailing spaces and collapses runs of blank lines; page breaks are kept.</summary>
    public static string Tidy(string text)
    {
        StringBuilder builder = new(text.Length);
        int blank = 0;
        foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n'))
        {
            string line = raw.TrimEnd(' ', '\t');
            if (line.Length == 0)
            {
                if (builder.Length > 0 && ++blank == 1)
                {
                    builder.Append('\n');
                }

                continue;
            }

            blank = 0;
            builder.Append(line).Append('\n');
        }

        return builder.ToString().Trim('\n', ' ');
    }

    /// <summary>Cuts <paramref name="text"/> to at most <paramref name="maximumBytes"/> UTF-8 bytes on a character boundary.</summary>
    public static (string Text, bool Truncated) Bound(string text, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(text) <= maximumBytes)
        {
            return (text, false);
        }

        int low = 0;
        int high = Math.Min(text.Length, maximumBytes);
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (Encoding.UTF8.GetByteCount(text.AsSpan(0, middle)) <= maximumBytes)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (low > 0 && char.IsHighSurrogate(text[low - 1]))
        {
            low--;
        }

        return (text[..low], true);
    }

    /// <summary>True when the bytes decode as text without control characters other than tab, newline, carriage return and form feed.</summary>
    public static bool LooksLikeText(string text) =>
        !text.Any(c => (c < 0x20 && c is not ('\n' or '\r' or '\t' or '\f')) || c == 0x7f);
}
