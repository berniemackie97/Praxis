using System.Text;

namespace Praxis.Storage.Enrichment.Semantic;

/// <summary>
/// Finds a model-quoted passage in the text it was given. An exact ordinal match wins;
/// otherwise runs of whitespace are treated as equivalent, which tolerates reflowed line
/// breaks but never changed words. Returns the span in the original text, or null.
/// </summary>
internal static class EvidenceLocator
{
    internal const int MinimumQuote = 3;

    internal static (int Offset, int Length)? Find(string text, string quote)
    {
        string trimmed = quote.Trim();
        if (trimmed.Length < MinimumQuote)
        {
            return null;
        }

        int exact = text.IndexOf(trimmed, StringComparison.Ordinal);
        if (exact >= 0)
        {
            return (exact, trimmed.Length);
        }

        (string normalizedText, int[] map) = Collapse(text);
        (string normalizedQuote, _) = Collapse(trimmed);
        int found = normalizedText.IndexOf(normalizedQuote, StringComparison.Ordinal);
        if (found < 0)
        {
            return null;
        }

        int start = map[found];
        int end = map[found + normalizedQuote.Length - 1] + 1;
        return (start, end - start);
    }

    /// <summary>Collapses whitespace runs to one space, mapping each output char to its source index.</summary>
    private static (string Text, int[] Map) Collapse(string value)
    {
        StringBuilder builder = new StringBuilder(value.Length);
        List<int> map = new List<int>(value.Length);
        bool inWhitespace = false;
        for (int index = 0; index < value.Length; index++)
        {
            if (char.IsWhiteSpace(value[index]))
            {
                if (!inWhitespace)
                {
                    builder.Append(' ');
                    map.Add(index);
                    inWhitespace = true;
                }

                continue;
            }

            builder.Append(value[index]);
            map.Add(index);
            inWhitespace = false;
        }

        return (builder.ToString(), map.ToArray());
    }
}
