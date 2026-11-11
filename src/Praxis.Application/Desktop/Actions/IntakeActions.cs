using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Intake;
using Praxis.Application.Processing;
using Praxis.Domain.Intake;

namespace Praxis.Application.Desktop.Actions;

/// <summary>Health and owner controls of supervised folder intake.</summary>
public sealed class IntakeActions(IIntakeSupervisor? intake = null, IProcessingQueue? processingQueue = null,
    IIntakeIncidentJournal? incidents = null) : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.Of("intakeStatus", _ => Respond(Supervisor().Snapshot())),
        DesktopAction.Of("intakePause", fields =>
        {
            bool paused = fields.RequireBoolean("paused");
            return Respond(Supervisor().SetPaused(paused));
        }),
        DesktopAction.Of("intakeRescan", _ => Respond(Supervisor().RequestRescan())),
        DesktopAction.Of("intakeApproveRoot", fields =>
        {
            IIntakeSupervisor supervisor = intake ?? throw new InvalidOperationException("Folder reapproval requires supervised intake.");
            return Respond(supervisor.ApproveRoot(fields.RequireString("path", 4_096)));
        })
    ];

    private IIntakeSupervisor Supervisor() => intake ?? throw new InvalidOperationException("Supervised intake is not configured.");

    private IntakeResponse Respond(IntakeStatus status) =>
        new(status, intake?.RootStatuses(), processingQueue?.Backlog(), incidents?.Recent(20));
}
