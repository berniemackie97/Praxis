using System.Text;
using System.Xml;

namespace Praxis.Application.Documents.Ooxml;

/// <summary>
/// Word documents (DOCX/DOCM/DOTX): body paragraphs in order, tables as <c>a | b</c> rows,
/// tabs and breaks preserved, then headers, footers, footnotes and comments. Macros are never
/// read or run.
/// </summary>
public sealed class WordDocumentReader : IDocumentReader
{
    private const string Body = "word/document.xml";

    public IReadOnlyCollection<string> Extensions { get; } = [".docx", ".docm", ".dotx"];

    public bool CanRead(DocumentInput input) => OoxmlReading.IsZip(input) && (Extensions.Contains(input.Extension) || HasBody(input.Bytes));

    public Task<ReadOutcome> ReadAsync(DocumentInput input, CancellationToken cancellationToken) => Task.FromResult(OoxmlReading.Guard(() =>
    {
        using OoxmlPackage? package = OoxmlPackage.Open(input.Bytes, out string? reason);
        if (package is null)
        {
            return ReadOutcome.Unreadable(reason!);
        }

        if (!package.Contains(Body))
        {
            return ReadOutcome.Unreadable("docx_missing_body");
        }

        StringBuilder text = new();
        Append(package, Body, text);
        foreach (string part in package.Parts("word/header").Concat(package.Parts("word/footer"))
                     .Concat(["word/footnotes.xml", "word/endnotes.xml", "word/comments.xml"]).Where(package.Contains))
        {
            cancellationToken.ThrowIfCancellationRequested();
            text.Append('\n');
            Append(package, part, text);
        }

        return ReadOutcome.Read(new ReadDocument(DocumentFormats.WordProcessing, text.ToString(), Title: package.Title()));
    }));

    private static void Append(OoxmlPackage package, string part, StringBuilder text)
    {
        using XmlReader reader = package.Xml(part)!;
        StringBuilder paragraph = new();
        List<string> cells = [];
        int tableDepth = 0;
        reader.MoveToContent();
        while (!reader.EOF)
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "t" or "delText" when !reader.IsEmptyElement:
                        paragraph.Append(reader.ReadElementContentAsString());
                        continue;
                    case "tab":
                        paragraph.Append('\t');
                        break;
                    case "br" or "cr":
                        paragraph.Append('\n');
                        break;
                    case "tbl":
                        tableDepth++;
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                switch (reader.LocalName)
                {
                    case "p" when tableDepth > 0:
                        paragraph.Append(' ');
                        break;
                    case "p":
                        text.Append(paragraph.ToString().Trim()).Append('\n');
                        paragraph.Clear();
                        break;
                    case "tc":
                        cells.Add(paragraph.ToString().Trim());
                        paragraph.Clear();
                        break;
                    case "tr":
                        text.AppendJoin(" | ", cells).Append('\n');
                        cells.Clear();
                        break;
                    case "tbl":
                        tableDepth--;
                        text.Append('\n');
                        break;
                }
            }

            // ReadElementContentAsString already advanced past its element; everything else advances here.
            reader.Read();
        }
    }

    private static bool HasBody(byte[] bytes)
    {
        using OoxmlPackage? package = OoxmlPackage.Open(bytes, out _);
        return package?.Contains(Body) == true;
    }
}
