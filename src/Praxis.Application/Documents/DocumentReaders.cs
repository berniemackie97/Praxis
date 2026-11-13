namespace Praxis.Application.Documents;

/// <summary>
/// The ordered reader registry. Readers that recognize a content signature come first and the
/// plain-text reader last, so a mislabeled file is read by what it is, not by its name.
/// </summary>
public sealed class DocumentReaders
{
    private static readonly HashSet<string> VerbatimFormats = new(StringComparer.Ordinal)
    {
        DocumentFormats.Text, DocumentFormats.Markdown, DocumentFormats.Code, DocumentFormats.Subtitles
    };

    private readonly IReadOnlyList<IDocumentReader> readers;

    public DocumentReaders(IEnumerable<IDocumentReader> readers)
    {
        this.readers = readers.ToArray();
        Extensions = this.readers.SelectMany(reader => reader.Extensions).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    public IReadOnlyList<string> Extensions { get; }

    public IDocumentReader? Find(DocumentInput input) => readers.FirstOrDefault(reader => reader.CanRead(input));

    /// <summary>Reads with the first matching reader and bounds the text to the stored derivative limit.</summary>
    public async Task<ReadOutcome> ReadAsync(DocumentInput input, CancellationToken cancellationToken)
    {
        if (input.Bytes.Length > DocumentLimits.MaximumOriginalBytes)
        {
            return ReadOutcome.Unreadable("document_too_large");
        }

        if (Find(input) is not { } reader)
        {
            // "unsupported_format" means no reader exists yet (reopened when one is added); content a
            // reader's extension claims but whose bytes match no format stays closed.
            return ReadOutcome.Unreadable(Extensions.Contains(input.Extension) ? "content_unrecognized" : "unsupported_format");
        }

        ReadOutcome outcome = await reader.ReadAsync(input, cancellationToken);
        if (outcome.Document is not { } document)
        {
            return outcome;
        }

        // Text the owner wrote is kept byte-for-byte (spans and citations point into it); text a
        // reader derived from a richer format is normalized.
        bool verbatim = VerbatimFormats.Contains(document.Format);
        string text = verbatim ? document.Text : TextShape.Tidy(document.Text);
        if (!verbatim && text.Length == 0)
        {
            return ReadOutcome.Unreadable("no_text_found");
        }

        (string bounded, bool truncated) = TextShape.Bound(text, DocumentLimits.MaximumTextBytes);
        return ReadOutcome.Read(document with { Text = bounded, Truncated = document.Truncated || truncated });
    }
}
