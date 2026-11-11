using Praxis.Domain.Processing;
using Praxis.Domain.Text;

namespace Praxis.Application.Text;

/// <summary>
/// Structure-aware segments of a revision's extracted text, stored as offsets into the text
/// derivative. Readable only for the current revision of an active source.
/// </summary>
public interface ITextSegmentRepository
{
    IReadOnlyList<TextSegment> List(Guid itemId, int revision);

    /// <summary>
    /// Completed segment spans of one revision for a downstream processor that already holds a
    /// verified copy of the text (see <see cref="ITextDerivativeRepository.ReadLeasedInput"/>).
    /// </summary>
    IReadOnlyList<TextSegmentSpan> ReadCompleted(Guid itemId, int revision);

    /// <summary>Stores the segments and completes the leased job in one transaction.</summary>
    bool Commit(ProcessingLease lease, LeasedText input, IReadOnlyList<TextSegmentSpan> segments);
}
