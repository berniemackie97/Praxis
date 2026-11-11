using Praxis.Domain.Intake;

namespace Praxis.Application.Intake;

/// <summary>Owner controls and health of supervised folder intake.</summary>
public interface IIntakeSupervisor
{
    IntakeStatus Snapshot();

    /// <summary>Per-folder status when several folders are supervised; null for a single folder.</summary>
    IReadOnlyList<WatchedRootStatus>? RootStatuses();

    IntakeStatus SetPaused(bool paused);

    IntakeStatus RequestRescan();

    /// <summary>Re-approves a supervised folder after it was replaced or reattached.</summary>
    IntakeStatus ApproveRoot(string selectedPath);

    /// <summary>Throws when the primary folder, which receives quick notes, is not approved and available.</summary>
    void EnsurePrimaryRootApproved();
}
