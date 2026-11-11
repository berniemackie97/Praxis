namespace Praxis.Notes.Synchronization;

public enum NoteSubmitStatus
{
    Applied,
    StaleRevision,
    PathCollision,
    Unauthorized,
    WrongCollectionEpoch,
    InvalidRequest,
    UnsupportedProtocolVersion,
    OperationKeyReused
}
