using Praxis.Infrastructure.Catalog;
using Praxis.Infrastructure.Processing;

namespace Praxis.Infrastructure.Enrichment;

/// <summary>
/// Deterministic topic proposals from literal first- to third-level Markdown headings of
/// verified extracted text. Non-Markdown sources are marked unsupported; no model is used.
/// </summary>
public sealed class LocalHeadingProposer(LocalCatalog catalog) : ILocalProcessingHandler
{
    public string ProcessorKey => LocalProcessors.MarkdownHeadings;
    public TimeSpan LeaseDuration => TimeSpan.FromMinutes(1);
    public int MaxConcurrency => 1;

    public bool TryReopenWork() => false;

    public Task ProcessAsync(ProcessingLease lease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LeasedText? candidate = catalog.Text.ReadLeasedInput(lease);
        if (candidate is null)
        {
            return Task.CompletedTask;
        }

        string extension = Path.GetExtension(candidate.Locator).ToLowerInvariant();
        if (candidate.SourceKind != "file" || extension is not (".md" or ".markdown"))
        {
            catalog.Processing.Finish(lease, ProcessingCompletion.Unsupported);
            return Task.CompletedTask;
        }

        catalog.Proposals.Commit(lease, candidate, ExtractHeadings(candidate.Content));
        return Task.CompletedTask;
    }

    private static IReadOnlyList<ProposalCandidate> ExtractHeadings(string text)
    {
        List<ProposalCandidate> headings = new(8);
        bool fenced = false;
        for (int offset = 0; offset < text.Length && headings.Count < 8;)
        {
            int end = text.IndexOf('\n', offset);
            if (end < 0)
            {
                end = text.Length;
            }

            ReadOnlySpan<char> line = text.AsSpan(offset, end - offset).TrimEnd('\r');
            int indent = 0;
            while (indent < line.Length && indent < 4 && line[indent] == ' ')
            {
                indent++;
            }

            ReadOnlySpan<char> fragment = line[indent..];
            if (indent <= 3 && (fragment.StartsWith("~~~", StringComparison.Ordinal) || fragment.Length >= 3 && fragment[0] == (char)96
                    && fragment[1] == (char)96 && fragment[2] == (char)96))
            {
                fenced = !fenced;
            }
            else if (!fenced && indent <= 3)
            {
                int level = 0;
                while (level < fragment.Length && level < 3 && fragment[level] == '#')
                {
                    level++;
                }

                if (level is >= 1 and <= 3 && level < fragment.Length && fragment[level] is ' ' or '\t')
                {
                    int start = level;
                    while (start < fragment.Length && fragment[start] is ' ' or '\t')
                    {
                        start++;
                    }

                    ReadOnlySpan<char> value = fragment[start..].TrimEnd();
                    if (value.Length is >= 3 and <= 80 && value.ToString().Any(char.IsLetterOrDigit))
                    {
                        headings.Add(new ProposalCandidate("topic", null, value.ToString(),
                            offset + indent + start, value.Length));
                    }
                }
            }

            offset = end == text.Length ? text.Length : end + 1;
        }

        return headings;
    }
}
