namespace Praxis.Application.Media;

/// <summary>One recognized utterance with its time range in the media.</summary>
public sealed record TranscriptSegment(long StartMilliseconds, long EndMilliseconds, string Text);
