using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Knowledge;

namespace Praxis.Application.Desktop.Actions;

/// <summary>Owner governance of the entity layer: browse, rename, type, merge, reject, restore and relate.</summary>
public sealed class KnowledgeActions(IKnowledgeRepository knowledge) : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.Of("entities", fields => new EntitiesResponse(knowledge.ListEntities(
            fields.OptionalText("entityType", 32, "The entity type is invalid."),
            fields.OptionalText("search", 120, "The entity search is invalid."),
            fields.IntegerOrDefault("limit", 100, 1, 500), fields.OptionalBoolean("includeSuggestions") ?? false))),
        DesktopAction.Of("entity", fields => new EntityResponse(knowledge.GetEntity(fields.RequireGuid("entityId")))),
        DesktopAction.Of("entityRename", fields => new ChangedResponse(knowledge.Rename(
            fields.RequireGuid("entityId"), fields.RequireString("name", 120)))),
        DesktopAction.Of("entitySetType", fields => new ChangedResponse(knowledge.SetType(
            fields.RequireGuid("entityId"), fields.RequireString("entityType", 32)))),
        DesktopAction.Of("entityMerge", fields => new ChangedResponse(knowledge.Merge(
            fields.RequireGuid("entityId"), fields.RequireGuid("targetEntityId")))),
        DesktopAction.Of("entityReject", fields => new ChangedResponse(knowledge.Reject(fields.RequireGuid("entityId")))),
        DesktopAction.Of("entityRestore", fields => new ChangedResponse(knowledge.Restore(fields.RequireGuid("entityId")))),
        DesktopAction.Of("entityRelate", fields => new EntityRelationResponse(knowledge.Relate(
            fields.RequireGuid("entityId"), fields.RequireString("predicate", 32), fields.RequireGuid("targetEntityId"))))
    ];
}
