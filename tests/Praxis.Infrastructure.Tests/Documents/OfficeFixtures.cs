using System.IO.Compression;
using System.Text;

namespace Praxis.Infrastructure.Tests.Documents;

/// <summary>Minimal, valid Office Open XML packages built in memory for reader tests.</summary>
internal static class OfficeFixtures
{
    private const string Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string Sheet = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string Relationships = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    internal static byte[] Docx(string title, params string[] paragraphs) => Package(new Dictionary<string, string>
    {
        ["[Content_Types].xml"] = """<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>""",
        ["docProps/core.xml"] = $"""<?xml version="1.0"?><cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>{title}</dc:title></cp:coreProperties>""",
        ["word/document.xml"] = $"""
            <?xml version="1.0"?><w:document xmlns:w="{Word}"><w:body>
            {string.Concat(paragraphs.Select(paragraph => $"<w:p><w:r><w:t xml:space=\"preserve\">{paragraph}</w:t></w:r></w:p>"))}
            <w:tbl><w:tr><w:tc><w:p><w:r><w:t>Item</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Cost</w:t></w:r></w:p></w:tc></w:tr>
            <w:tr><w:tc><w:p><w:r><w:t>Cabinets</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>3,480.00</w:t></w:r></w:p></w:tc></w:tr></w:tbl>
            </w:body></w:document>
            """
    });

    internal static byte[] Xlsx() => Package(new Dictionary<string, string>
    {
        ["[Content_Types].xml"] = """<?xml version="1.0"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"/>""",
        ["xl/workbook.xml"] = $"""<?xml version="1.0"?><workbook xmlns="{Sheet}" xmlns:r="{Relationships}"><sheets><sheet name="Budget" sheetId="1" r:id="rId1"/></sheets></workbook>""",
        ["xl/_rels/workbook.xml.rels"] = """<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="worksheet" Target="worksheets/sheet1.xml"/></Relationships>""",
        ["xl/sharedStrings.xml"] = $"""<?xml version="1.0"?><sst xmlns="{Sheet}"><si><t>Date</t></si><si><t>Description</t></si><si><t>Amount</t></si><si><t>Market Fresh</t></si></sst>""",
        ["xl/worksheets/sheet1.xml"] = $"""
            <?xml version="1.0"?><worksheet xmlns="{Sheet}"><sheetData>
            <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c></row>
            <row r="2"><c r="A2" t="inlineStr"><is><t>2026-10-02</t></is></c><c r="B2" t="s"><v>3</v></c><c r="C2"><v>-84.12</v></c></row>
            </sheetData></worksheet>
            """
    });

    /// <summary>A package with one highly compressible entry, to exercise the decompression-ratio guard.</summary>
    internal static byte[] Bomb()
    {
        using MemoryStream buffer = new();
        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("word/document.xml", CompressionLevel.SmallestSize);
            using Stream stream = entry.Open();
            stream.Write(new byte[8 * 1024 * 1024]);
        }

        return buffer.ToArray();
    }

    private static byte[] Package(Dictionary<string, string> parts)
    {
        using MemoryStream buffer = new();
        using (ZipArchive archive = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string content) in parts)
            {
                using Stream stream = archive.CreateEntry(name).Open();
                stream.Write(Encoding.UTF8.GetBytes(content.Trim()));
            }
        }

        return buffer.ToArray();
    }
}
