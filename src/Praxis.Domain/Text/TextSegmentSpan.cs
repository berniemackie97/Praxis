namespace Praxis.Domain.Text;

public readonly record struct TextSegmentSpan(int Start, int Length, string? HeadingPath);
