namespace Praxis.Domain.Media;

public sealed record VideoCueHit(Guid CaptionSourceId, Guid CaptionItemId, int CaptionRevision, string CaptionSha256, int CaptionOffset, int CaptionLength, int CueNumber, long StartMilliseconds, long EndMilliseconds, string CaptionText, string Producer, Guid VideoSourceId, Guid VideoItemId, int VideoRevision, string VideoSha256, string VideoLocator);
