namespace Praxis.Domain.Media;

/// <summary>A character range of a source's text and the time range in its media where it is spoken.</summary>
public sealed record TimedSpan(int Offset, int Length, long StartMilliseconds, long EndMilliseconds)
{
    public const int MaximumSpans = 50_000;
}
