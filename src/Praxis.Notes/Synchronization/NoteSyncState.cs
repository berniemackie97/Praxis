namespace Praxis.Notes.Synchronization;

public enum NoteSyncState
{
    UpToDate,
    MoreWork,
    EpochChanged,
    CursorResetRequired
}
