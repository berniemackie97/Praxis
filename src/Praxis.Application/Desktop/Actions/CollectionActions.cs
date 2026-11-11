using Praxis.Application.Collections;
using Praxis.Application.Desktop.Contracts;

namespace Praxis.Application.Desktop.Actions;

/// <summary>Owner-authored collections and their source memberships.</summary>
public sealed class CollectionActions(ICollectionRepository collections) : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.Of("collections", fields => new CollectionsResponse(collections.List(
            fields.IntegerOrDefault("limit", 100, 1, 200), fields.IntegerOrDefault("offset", 0, 0, 1_000_000)))),
        DesktopAction.Of("collectionCreate", fields => new CollectionResponse(collections.Create(fields.RequireString("name", 80)))),
        DesktopAction.Of("collectionRename", fields => new ChangedResponse(collections.Rename(
            fields.RequireGuid("collectionId"), fields.RequireString("name", 80)))),
        DesktopAction.Of("collectionArchive", fields => new ChangedResponse(collections.Archive(fields.RequireGuid("collectionId")))),
        DesktopAction.Of("collectionAdd", fields => new ChangedResponse(collections.AddSource(
            fields.RequireGuid("collectionId"), fields.RequireGuid("sourceId")))),
        DesktopAction.Of("collectionRemove", fields => new ChangedResponse(collections.RemoveSource(
            fields.RequireGuid("collectionId"), fields.RequireGuid("sourceId")))),
        DesktopAction.Of("sourceCollections", fields => new SourceCollectionsResponse(collections.ListForSource(
            fields.RequireGuid("sourceId"), 100)))
    ];
}
