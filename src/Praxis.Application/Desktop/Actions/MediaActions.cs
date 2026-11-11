using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Media;

namespace Praxis.Application.Desktop.Actions;

/// <summary>Caption cue search and verified video header inspection.</summary>
public sealed class MediaActions(MediaEvidence media) : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.Of("mediaCues", fields => new MediaCuesResponse(media.SearchCues(
            fields.RequireString("query", 256), fields.IntegerOrDefault("limit", 20, 1, 50),
            fields.OptionalGuid("collectionId")))),
        DesktopAction.OfAsync("mediaInspection", async (fields, cancellationToken) =>
            new MediaInspectionResponse(await media.InspectAsync(fields.RequireGuid("sourceId"),
                fields.OptionalInteger("revision", 1, int.MaxValue), cancellationToken)))
    ];
}
