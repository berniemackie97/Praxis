namespace Praxis.Domain.Media;

public sealed record TimedCue(int Number, long StartMilliseconds, long EndMilliseconds, int Offset, int Length, string Text);
