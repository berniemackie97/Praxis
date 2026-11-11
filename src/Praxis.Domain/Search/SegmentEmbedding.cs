namespace Praxis.Domain.Search;

/// <summary>A unit-length embedding of one <see cref="SemanticSegment"/>.</summary>
public sealed record SegmentEmbedding(SemanticSegment Segment, float[] Vector);
