using Praxis.Application.Processing;
using Praxis.Domain.Processing;
using Praxis.Domain.Text;

namespace Praxis.Application.Text;

/// <summary>
/// Segments each revision's completed text so later processors can work on bounded,
/// citable spans. Markdown structure (headings, fenced code) is respected; other text splits
/// at blank lines and line boundaries.
/// </summary>
public sealed class TextSegmentationHandler(
    ITextDerivativeRepository derivatives, ITextSegmentRepository segmentStore, IProcessingQueue processing) : IProcessingHandler
{
    public string ProcessorKey => ProcessorKeys.TextSegments;
    public TimeSpan LeaseDuration => TimeSpan.FromMinutes(1);
    public int MaxConcurrency => 1;

    public bool TryReopenWork() => false;

    public Task ProcessAsync(ProcessingLease lease, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        LeasedText? input = derivatives.ReadLeasedInput(lease);
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
            processing.Finish(lease, ProcessingCompletion.Failed, "segment_limit");
            return Task.CompletedTask;
        }

        segmentStore.Commit(lease, input, segments);
        return Task.CompletedTask;
    }
}
