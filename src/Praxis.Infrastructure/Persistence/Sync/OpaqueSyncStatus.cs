namespace Praxis.Infrastructure.Persistence.Sync;

public enum OpaqueSyncStatus
{
    Applied,
    Ok,
    StaleRevision,
    InvalidRequest,
    WrongCollectionEpoch,
    UnsupportedProtocolVersion,
    OperationKeyReused,
    InvalidCursor,
    CursorExpired,
    ResetRequired,
    QuotaExceeded
}
