using Praxis.Domain.Processing;
using Praxis.Domain.Text;
using Praxis.Infrastructure.Catalog;

namespace Praxis.Infrastructure.Processing;

/// <summary>
/// Segments each revision's completed text so later processors can work on bounded,
/// citable spans. Markdown structure (headings, fenced code) is respected; other text splits
/// at blank lines and line boundaries.
/// </summary>
public sealed class LocalTextSegmenter(LocalCatalog catalog) : ILocalProcessingHandler
{
    public string ProcessorKey => LocalProcessors.TextSegments;
    public TimeSpan LeaseDuration => TimeSpan.FromMinutes(1);
    public int MaxConcurrency => 1;

    public bool TryReopenWork() => false;

    public Task ProcessAsync(ProcessingLease lease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LeasedText? input = catalog.Text.ReadLeasedInput(lease);
        if (input is null)
        {
            return Task.CompletedTask;
        }

        string extension = Path.GetExtension(input.Locator).ToLowerInvariant();
        IReadOnlyList<TextSegmentSpan> segments;
        try
        {
            segments = TextSegmenter.Segment(input.Content, extension is ".md" or ".markdown");
        }
        catch (InvalidDataException)
        {
            catalog.Processing.Finish(lease, ProcessingCompletion.Failed, "segment_limit");
            return Task.CompletedTask;
        }

        catalog.Segments.Commit(lease, input, segments);
        return Task.CompletedTask;
    }
}
