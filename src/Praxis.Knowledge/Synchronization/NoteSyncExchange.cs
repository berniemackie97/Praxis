using Praxis.Knowledge.Journal;

namespace Praxis.Knowledge.Synchronization;

/// <summary>
/// Client-side projections after an authenticated and decrypted exchange. These
/// contracts are not an HTTP wire format. A remote adapter must enforce device,
/// security-space and key-epoch authorization and never transmit plaintext for
/// client-held encrypted spaces.
/// </summary>
public interface INoteSyncExchange
{
    Task<NoteSubmitResult> SubmitAsync(NoteSubmitRequest request, CancellationToken cancellationToken);
    Task<NotePullResult> PullAsync(NotePullRequest request, CancellationToken cancellationToken);
    Task<NoteSnapshotResult> SnapshotAsync(Guid securitySpaceId, Guid collectionEpoch,
        CancellationToken cancellationToken);
}

public sealed record NoteSubmitRequest(
    int ProtocolVersion, Guid SecuritySpaceId, Guid CollectionEpoch, Guid DeviceId,
    PendingNoteEdit Edit);

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

public sealed record NoteSubmitResult(
    NoteSubmitStatus Status, Guid SecuritySpaceId, Guid CollectionEpoch,
    Guid DeviceId, Guid OperationId, long Sequence, LocalNoteState? Current);

public sealed record NotePullRequest(
    int ProtocolVersion, Guid SecuritySpaceId, Guid CollectionEpoch,
    long AfterSequence, int Limit);

public enum NotePullStatus
{
    Ok,
    CursorExpired,
    WrongCollectionEpoch,
    InvalidCursor,
    Unauthorized,
    UnsupportedProtocolVersion
}

public sealed record NotePullResult(
    NotePullStatus Status, Guid SecuritySpaceId, Guid CollectionEpoch,
    long RequestedCursor, long NextCursor, long HeadSequence,
    IReadOnlyList<AppliedNoteChange> Changes);

public sealed record NoteSnapshotResult(
    Guid SecuritySpaceId, LocalNoteSnapshot Snapshot);

public enum NoteSyncState
{
    UpToDate,
    MoreWork,
    EpochChanged,
    CursorResetRequired
}

public sealed record NoteSyncProgress(
    NoteSyncState State, int AcceptedOperations, int NewConflicts, int AppliedChanges,
    bool SnapshotApplied, long DurableCursor, int PendingOperations, int UnresolvedConflicts);
