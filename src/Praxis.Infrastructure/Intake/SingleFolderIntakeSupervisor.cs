using Praxis.Application.Intake;
using Praxis.Domain.Intake;

namespace Praxis.Infrastructure.Intake;

/// <summary>Owner controls for one supervised folder; folder re-approval needs a supervised group.</summary>
public sealed class SingleFolderIntakeSupervisor(WatchedFolderIntake monitor) : IIntakeSupervisor
{
    public IntakeStatus Snapshot() => monitor.Snapshot();

    public IReadOnlyList<WatchedRootStatus>? RootStatuses() => null;

    public IntakeStatus SetPaused(bool paused) => monitor.SetPaused(paused);

    public IntakeStatus RequestRescan() => monitor.RequestRescan();

    public IntakeStatus ApproveRoot(string selectedPath) =>
        throw new InvalidOperationException("Folder reapproval requires supervised intake.");

    public void EnsurePrimaryRootApproved()
    {
    }
}
