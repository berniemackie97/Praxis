using System.Text;

namespace Praxis.Application.Documents.Tables;

/// <summary>
/// CSV and TSV exports: rows are kept as structure (so later stages can map columns once and
/// apply the mapping in code) and rendered as <c>a | b | c</c> lines for search and reading.
/// Content that is not consistently delimited is read as plain text.
/// </summary>
public sealed class DelimitedDocumentReader : IDocumentReader
{
    public IReadOnlyCollection<string> Extensions { get; } = [".csv", ".tsv", ".tab"];

    public bool CanRead(DocumentInput input) => Extensions.Contains(input.Extension) && !BinarySignature.Matches(input.Bytes);

    public Task<ReadOutcome> ReadAsync(DocumentInput input, CancellationToken cancellationToken)
    {
        string? text = TextDecoder.Decode(input.Bytes);
        if (text is null || !TextShape.LooksLikeText(text))
        {
            return Task.FromResult(ReadOutcome.Unreadable("binary_content"));
        }

        IReadOnlyList<DelimitedRow>? rows = DelimitedReader.DetectDelimiter(text) is { } delimiter
            ? DelimitedReader.Read(text, delimiter, DocumentLimits.MaximumTableRows + 1)
            : null;
        if (rows is null || rows.Count < 2)
        {
            return Task.FromResult(ReadOutcome.Read(new ReadDocument(DocumentFormats.Text, text)));
        }

        bool truncated = rows.Count > DocumentLimits.MaximumTableRows;
        rows = truncated ? rows.Take(DocumentLimits.MaximumTableRows).ToArray() : rows;
        return Task.FromResult(ReadOutcome.Read(new ReadDocument(DocumentFormats.Delimited, Render(rows), Rows: rows, Truncated: truncated)));
    }

    public static string Render(IEnumerable<DelimitedRow> rows)
    {
        StringBuilder builder = new();
        foreach (DelimitedRow row in rows)
        {
            builder.AppendJoin(" | ", row.Fields.Select(field => field.Trim())).Append('\n');
        }

        return builder.ToString();
    }
}
