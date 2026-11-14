using Praxis.Application.Documents;

namespace Praxis.Application.Links;

/// <summary>Saved link files (<c>.webloc</c>, <c>.url</c>): read as the URL they point to, fetched later by the link stage.</summary>
public sealed class LinkReader : IDocumentReader
{
    public IReadOnlyCollection<string> Extensions { get; } = [".webloc", ".url"];

    public bool CanRead(DocumentInput input) => Extensions.Contains(input.Extension) && input.Bytes.Length <= 64 * 1024;

    public Task<ReadOutcome> ReadAsync(DocumentInput input, CancellationToken cancellationToken) =>
        Task.FromResult(SavedLink.FromFile(input.Extension, input.Bytes) is { } url
            ? ReadOutcome.Read(new ReadDocument(DocumentFormats.Link, SavedLink.ToText(url, Path.GetFileNameWithoutExtension(input.FileName)),
                Title: Path.GetFileNameWithoutExtension(input.FileName)))
            : ReadOutcome.Unreadable("link_unreadable"));
}
