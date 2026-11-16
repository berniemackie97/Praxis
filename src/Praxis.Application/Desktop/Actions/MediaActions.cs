using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Media;

namespace Praxis.Application.Desktop.Actions;

/// <summary>Caption cue search, verified video header inspection and playback of cited media.</summary>
public sealed class MediaActions(MediaEvidence media, MediaPlayback playback) : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.Of("mediaCues", fields => new MediaCuesResponse(media.SearchCues(
            fields.RequireString("query", 256), fields.IntegerOrDefault("limit", 20, 1, 50),
            fields.OptionalGuid("collectionId")))),
        DesktopAction.OfAsync("mediaInspection", async (fields, cancellationToken) =>
            new MediaInspectionResponse(await media.InspectAsync(fields.RequireGuid("sourceId"),
                fields.OptionalInteger("revision", 1, int.MaxValue), cancellationToken))),
        DesktopAction.OfAsync("mediaPlayback", async (fields, cancellationToken) =>
            new MediaPlaybackResponse(await playback.ResolveAsync(fields.RequireGuid("sourceId"), cancellationToken,
                fields.RequireGuid("itemId"), fields.OptionalInteger("revision", 1, int.MaxValue) ?? throw new ArgumentException("A cited media revision is required."),
                fields.OptionalText("timebase", 32, "The playback timebase is invalid."), citedClip: true)))
    ];
}
