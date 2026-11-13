using System.Globalization;
using System.Text;
using System.Xml;
using Praxis.Application.Documents.Tables;

namespace Praxis.Application.Documents.Ooxml;

/// <summary>
/// Excel workbooks (XLSX/XLSM): every sheet in workbook order, rows as <c>a | b | c</c> lines
/// under a sheet heading, shared and inline strings resolved, formulas read as their cached
/// values (never evaluated). The largest sheet is also kept as table rows so column mappings can
/// be applied in code.
/// </summary>
public sealed class SpreadsheetReader : IDocumentReader
{
    private const string Workbook = "xl/workbook.xml";

    public IReadOnlyCollection<string> Extensions { get; } = [".xlsx", ".xlsm", ".xltx"];

    public bool CanRead(DocumentInput input) => OoxmlReading.IsZip(input) && (Extensions.Contains(input.Extension) || HasWorkbook(input.Bytes));

    public Task<ReadOutcome> ReadAsync(DocumentInput input, CancellationToken cancellationToken) => Task.FromResult(OoxmlReading.Guard(() =>
    {
        using OoxmlPackage? package = OoxmlPackage.Open(input.Bytes, out string? reason);
        if (package is null)
        {
            return ReadOutcome.Unreadable(reason!);
        }

        if (!package.Contains(Workbook))
        {
            return ReadOutcome.Unreadable("xlsx_missing_workbook");
        }

        IReadOnlyList<string> shared = SharedStrings(package);
        Dictionary<string, string> targets = Relationships(package);
        StringBuilder text = new();
        IReadOnlyList<DelimitedRow> largest = [];
        int totalRows = 0;
        bool truncated = false;
        foreach ((string name, string relationship) in Sheets(package))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!targets.TryGetValue(relationship, out string? target))
            {
                continue;
            }

            List<DelimitedRow> rows = ReadSheet(package, "xl/" + target.TrimStart('/').Replace("xl/", string.Empty, StringComparison.Ordinal), shared,
                DocumentLimits.MaximumTableRows - totalRows);
            totalRows += rows.Count;
            truncated |= totalRows >= DocumentLimits.MaximumTableRows;
            text.Append("Sheet: ").Append(name).Append('\n').Append(DelimitedDocumentReader.Render(rows)).Append('\n');
            if (rows.Count > largest.Count)
            {
                largest = rows;
            }
        }

        return ReadOutcome.Read(new ReadDocument(DocumentFormats.Spreadsheet, text.ToString(), Title: package.Title(),
            Rows: largest.Count >= 2 ? largest : null, Truncated: truncated));
    }));

    private static List<DelimitedRow> ReadSheet(OoxmlPackage package, string part, IReadOnlyList<string> shared, int budget)
    {
        List<DelimitedRow> rows = [];
        using XmlReader? reader = package.Xml(part);
        if (reader is null)
        {
            return rows;
        }

        SortedDictionary<int, string> cells = [];
        int rowNumber = 0;
        int column = 0;
        string? type = null;
        reader.MoveToContent();
        while (!reader.EOF && rows.Count < budget)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "row":
                        rowNumber = int.TryParse(reader.GetAttribute("r"), NumberStyles.None, CultureInfo.InvariantCulture, out int r) ? r : rowNumber + 1;
                        cells.Clear();
                        column = 0;
                        break;
                    case "c":
                        column = ColumnIndex(reader.GetAttribute("r")) ?? column + 1;
                        type = reader.GetAttribute("t");
                        break;
                    case "v" when !reader.IsEmptyElement:
                        string raw = reader.ReadElementContentAsString();
                        cells[column] = type switch
                        {
                            "s" when int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < shared.Count => shared[index],
                            "b" => raw == "1" ? "TRUE" : "FALSE",
                            _ => raw
                        };
                        continue;
                    case "t" when type == "inlineStr" && !reader.IsEmptyElement:
                        cells[column] = (cells.GetValueOrDefault(column) ?? string.Empty) + reader.ReadElementContentAsString();
                        continue;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "row" && cells.Count > 0)
            {
                int width = cells.Keys.Max();
                rows.Add(new DelimitedRow(rowNumber, Enumerable.Range(1, width).Select(index => cells.GetValueOrDefault(index) ?? string.Empty).ToArray()));
            }

            // ReadElementContentAsString already advanced past its element; everything else advances here.
            reader.Read();
        }

        return rows;
    }

    private static IReadOnlyList<string> SharedStrings(OoxmlPackage package)
    {
        List<string> strings = [];
        using XmlReader? reader = package.Xml("xl/sharedStrings.xml");
        if (reader is null)
        {
            return strings;
        }

        StringBuilder current = new();
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "t" && !reader.IsEmptyElement)
            {
                current.Append(reader.ReadElementContentAsString());
                continue;
            }

            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "si")
            {
                strings.Add(current.ToString());
                current.Clear();
            }

            reader.Read();
        }

        return strings;
    }

    private static IEnumerable<(string Name, string Relationship)> Sheets(OoxmlPackage package)
    {
        List<(string, string)> sheets = [];
        using XmlReader reader = package.Xml(Workbook)!;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "sheet" &&
                reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships") is { } id)
            {
                sheets.Add((reader.GetAttribute("name") ?? "Sheet", id));
            }
        }

        return sheets;
    }

    private static Dictionary<string, string> Relationships(OoxmlPackage package)
    {
        Dictionary<string, string> targets = new(StringComparer.Ordinal);
        using XmlReader? reader = package.Xml("xl/_rels/workbook.xml.rels");
        while (reader is not null && reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element && reader.LocalName == "Relationship" &&
                reader.GetAttribute("Id") is { } id && reader.GetAttribute("Target") is { } target)
            {
                targets[id] = target;
            }
        }

        return targets;
    }

    /// <summary>The 1-based column of a cell reference such as <c>BC12</c>.</summary>
    public static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference))
        {
            return null;
        }

        int column = 0;
        foreach (char c in reference)
        {
            if (c is < 'A' or > 'Z')
            {
                break;
            }

            column = column * 26 + (c - 'A' + 1);
            if (column > 16_384)
            {
                return null;
            }
        }

        return column == 0 ? null : column;
    }

    private static bool HasWorkbook(byte[] bytes)
    {
        using OoxmlPackage? package = OoxmlPackage.Open(bytes, out _);
        return package?.Contains(Workbook) == true;
    }
}
