namespace Praxis.Application.Media;

/// <summary>The segments a transcription produced, or why it did not.</summary>
public sealed record TranscriptionResult(IReadOnlyList<TranscriptSegment>? Segments, string? ReasonCode)
{
    public static TranscriptionResult Recognized(IReadOnlyList<TranscriptSegment> segments) => new(segments, null);

    public static TranscriptionResult Failed(string reasonCode) => new(null, reasonCode);
}
