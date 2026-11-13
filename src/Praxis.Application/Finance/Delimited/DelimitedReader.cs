using System.Text;

namespace Praxis.Application.Finance.Delimited;

/// <summary>
/// RFC 4180 reading (quoted fields, doubled quotes, embedded line breaks, CRLF) with delimiter
/// detection across comma, semicolon, tab and pipe. Row and field counts are bounded.
/// </summary>
public static class DelimitedReader
{
    private const int MaximumFields = 256;
    private const int MaximumFieldLength = 4_096;
    private static readonly char[] Candidates = [',', ';', '\t', '|'];

    /// <summary>
    /// The delimiter that splits the most leading lines into the same number (at least two) of
    /// fields, or null when no candidate structures the text.
    /// </summary>
    public static char? DetectDelimiter(string text)
    {
        char? best = null;
        int bestScore = 0;
        foreach (char candidate in Candidates)
        {
            IReadOnlyList<DelimitedRow>? rows = Read(text, candidate, 40);
            if (rows is null)
            {
                continue;
            }

            int[] counts = rows.Select(row => row.Fields.Count).Where(count => count >= 2).ToArray();
            if (counts.Length == 0)
            {
                continue;
            }

            int score = counts.GroupBy(count => count).Max(group => group.Count() * Math.Min(group.Key, 12));
            if (score > bestScore)
            {
                (best, bestScore) = (candidate, score);
            }
        }

        return best;
    }

    /// <summary>Rows of <paramref name="text"/>, or null when a field or row bound is exceeded.</summary>
    public static IReadOnlyList<DelimitedRow>? Read(string text, char delimiter, int maximumRows)
    {
        List<DelimitedRow> rows = [];
        List<string> fields = [];
        StringBuilder field = new();
        bool quoted = false;
        bool fieldStarted = false;
        int line = 1;
        int rowLine = 1;
        for (int index = 0; index < text.Length; index++)
        {
            char c = text[index];
            if (quoted)
            {
                if (c == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    if (c == '\n')
                    {
                        line++;
                    }

                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0 && !fieldStarted)
            {
                quoted = true;
                fieldStarted = true;
            }
            else if (c == delimiter)
            {
                if (!AddField(fields, field))
                {
                    return null;
                }

                fieldStarted = false;
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    index++;
                }

                if (!AddField(fields, field) || !AddRow(rows, fields, rowLine, maximumRows))
                {
                    return rows.Count >= maximumRows ? rows : null;
                }

                fieldStarted = false;
                line++;
                rowLine = line;
            }
            else
            {
                field.Append(c);
                fieldStarted = true;
            }

            if (field.Length > MaximumFieldLength)
            {
                return null;
            }
        }

        if (field.Length > 0 || fields.Count > 0)
        {
            if (!AddField(fields, field))
            {
                return null;
            }

            AddRow(rows, fields, rowLine, maximumRows);
        }

        return rows;
    }

    private static bool AddField(List<string> fields, StringBuilder field)
    {
        if (fields.Count >= MaximumFields)
        {
            return false;
        }

        fields.Add(field.ToString().Trim());
        field.Clear();
        return true;
    }

    private static bool AddRow(List<DelimitedRow> rows, List<string> fields, int line, int maximumRows)
    {
        if (fields.Any(value => value.Length > 0))
        {
            if (rows.Count >= maximumRows)
            {
                return false;
            }

            rows.Add(new DelimitedRow(line, fields.ToArray()));
        }

        fields.Clear();
        return true;
    }
}
