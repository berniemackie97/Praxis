using Praxis.Application.Agents.Skills;
using Praxis.Application.Desktop.Contracts;

namespace Praxis.Application.Desktop.Actions;

/// <summary>Owner-only management of private drafts and installed skills, through the desktop pipe.</summary>
public sealed class SkillActions(SkillLibrary library) : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.Of("skillCatalog", _ => new SkillCatalogResponse(library.Catalog(includePrivate: true, enabledOnly: false))),
        DesktopAction.Of("skillRead", fields =>
            new SkillTextResponse(library.Open(fields.RequireString("name", SkillDocument.MaximumName), includePrivate: true, enabledOnly: false)?.Markdown
                ?? throw new ArgumentException("Unknown installed skill."))),
        DesktopAction.Of("skillHistory", fields => new SkillHistoryResponse(library.History(
            fields.OptionalText("name", SkillDocument.MaximumName, "The skill name is invalid."),
            fields.OptionalBoolean("pendingOnly") ?? false, fields.IntegerOrDefault("limit", 100, 1, 200)))),
        DesktopAction.Of("skillRevisionRead", fields =>
            library.ReadRevision(fields.RequireGuid("revisionId")) ?? throw new ArgumentException("Unknown skill revision.")),
        DesktopAction.Of("skillDraftImport", fields => library.Propose(fields.RequireString("markdown", SkillDocument.MaximumCharacters), null, "Imported by the owner.")),
        DesktopAction.Of("skillDraftResolve", fields => new ChangedResponse(library.Resolve(fields.RequireGuid("revisionId"), fields.RequireBoolean("accept")))),
        DesktopAction.Of("skillSetEnabled", fields => new ChangedResponse(library.SetEnabled(
            fields.RequireString("name", SkillDocument.MaximumName), fields.RequireBoolean("enabled"))))
    ];

    private sealed record SkillCatalogResponse(IReadOnlyList<SkillDescriptor> Skills);
    private sealed record SkillHistoryResponse(IReadOnlyList<SkillRevisionSummary> Revisions);
    private sealed record SkillTextResponse(string Markdown);
}
