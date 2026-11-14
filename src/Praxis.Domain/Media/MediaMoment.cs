namespace Praxis.Domain.Media;

/// <summary>
/// A time range in one media source and the words spoken in it. <see cref="SourceId"/> is the
/// source whose text holds the evidence (the media's own transcript, or a caption file); playback
/// uses <see cref="MediaSourceId"/>, which differs only for caption files paired with a video.
/// </summary>
public sealed record MediaMoment(
    Guid SourceId, Guid ItemId, int Revision, string SourceName, Guid MediaSourceId, long StartMilliseconds, long EndMilliseconds,
    string Text, int EvidenceOffset, int EvidenceLength, bool WholeMedia)
{
    /// <summary>The citation locator for this moment: <c>time:START-END</c> in seconds.</summary>
    public string Locator => TimeLocator.Format(StartMilliseconds, EndMilliseconds);
}
