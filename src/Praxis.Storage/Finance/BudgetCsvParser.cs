using System.Text;

namespace Praxis.Storage;

internal sealed record BudgetCsvRow(int Line, string[] Cells);

internal static class BudgetCsvParser
{
    internal static IReadOnlyList<BudgetCsvRow> Parse(string text, int maxRows)
    {
        var rows = new List<BudgetCsvRow>();
        var columns = new List<string>(8);
        var field = new StringBuilder();
        var line = 1;
        var rowStart = 1;
        var quoted = false;
        var closedQuote = false;
        var started = false;

        for (var i = 0; i <= text.Length; i++)
        {
            var end = i == text.Length;
            var c = end ? '\n' : text[i];

            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                        closedQuote = true;
                    }
                }
                else
                {
                    field.Append(c);
                    if (c == '\n')
                    {
                        line++;
                    }
                }
            }
            else if (c == ',' || c == '\n' || c == '\r' || end)
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    i++;
                }

                columns.Add(field.ToString());
                field.Clear();
                closedQuote = false;
                if (columns.Count > 8)
                {
                    throw new InvalidDataException($"Budget CSV row {rowStart} has too many columns.");
                }

                if (c != ',')
                {
                    if (started || columns.Any(value => value.Length > 0))
                    {
                        rows.Add(new BudgetCsvRow(rowStart, columns.ToArray()));
                        if (rows.Count > maxRows + 1)
                        {
                            throw new InvalidDataException("The financial CSV exceeds its row budget.");
                        }
                    }

                    columns.Clear();
                    started = false;
                    line++;
                    rowStart = line;
                }
                else
                {
                    started = true;
                }
            }
            else if (c == '"' && field.Length == 0 && !closedQuote)
            {
                quoted = true;
                started = true;
            }
            else if (closedQuote || c == '"')
            {
                throw new InvalidDataException($"Budget CSV row {rowStart} has invalid quotation.");
            }
            else
            {
                field.Append(c);
                started = true;
            }

            if (field.Length > 2_000)
            {
                throw new InvalidDataException($"Budget CSV row {rowStart} exceeds its field limit.");
            }
        }

        if (quoted)
        {
            throw new InvalidDataException("The financial CSV ends inside a quoted field.");
        }

        return rows;
    }
}
