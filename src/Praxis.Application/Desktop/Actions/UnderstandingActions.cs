using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Understanding;

namespace Praxis.Application.Desktop.Actions;

/// <summary>Intake state for first-party clients: how each source was read, triaged and understood, by which tier.</summary>
public sealed class UnderstandingActions(IUnderstandingRepository understanding) : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.Of("sourceUnderstanding", fields => new SourceUnderstandingResponse(understanding.ReadCurrent(fields.RequireGuid("sourceId"))))
    ];
}
