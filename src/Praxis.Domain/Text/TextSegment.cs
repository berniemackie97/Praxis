namespace Praxis.Domain.Text;

public sealed record TextSegment(
    Guid SourceId, Guid ItemId, int Revision, string SourceSha256,
    int Ordinal, int Start, int Length, string? HeadingPath, string Text);
