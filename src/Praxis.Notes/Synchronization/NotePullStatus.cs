namespace Praxis.Notes.Synchronization;

public enum NotePullStatus
{
    Ok,
    CursorExpired,
    WrongCollectionEpoch,
    InvalidCursor,
    Unauthorized,
    UnsupportedProtocolVersion,
    ResetRequired
}
