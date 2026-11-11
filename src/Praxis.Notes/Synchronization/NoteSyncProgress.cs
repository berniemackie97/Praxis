namespace Praxis.Notes.Synchronization;

public sealed record NoteSyncProgress(
    NoteSyncState State, int AcceptedOperations, int NewConflicts, int AppliedChanges,
    bool SnapshotApplied, long DurableCursor, int PendingOperations, int UnresolvedConflicts);
