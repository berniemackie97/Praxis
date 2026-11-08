namespace Praxis.Knowledge;

public enum LocalNoteAction
{
    Upsert,
    Delete,
    Restore
}

public enum LocalNoteConflictReason
{
    StaleRevision,
    PathCollision
}

public sealed record LocalNoteState(Guid Id, long Revision, string Path, string Markdown, bool Deleted);

public sealed record PendingNoteEdit(
    Guid OperationId, Guid RecordId, long ExpectedRevision,
    LocalNoteAction Action, string? Path, string? Markdown);

public sealed record RejectedNoteEdit(
    PendingNoteEdit Edit, LocalNoteConflictReason Reason, LocalNoteState? Current);

public sealed record AppliedNoteChange(long Sequence, LocalNoteState Record);

public sealed record AcceptedNoteReceipt(Guid OperationId, long Sequence, LocalNoteState Record);

public sealed record LocalNoteSnapshot(Guid CollectionEpoch, long Cursor, IReadOnlyList<LocalNoteState> Records);

internal sealed class LocalJournalState
{
    public Guid CollectionEpoch { get; set; }
    public Guid DeviceId { get; set; }
    public long Cursor { get; set; }
    public List<LocalNoteState> Records { get; set; } = [];
    public List<PendingNoteEdit> Pending { get; set; } = [];
    public List<RejectedNoteEdit> Conflicts { get; set; } = [];
    public List<RejectedNoteEdit> Resolved { get; set; } = [];
    public List<AcceptedNoteReceipt> AwaitingPull { get; set; } = [];

    public LocalJournalState Copy() => new()
    {
        CollectionEpoch = CollectionEpoch,
        DeviceId = DeviceId,
        Cursor = Cursor,
        Records = [.. Records],
        Pending = [.. Pending],
        Conflicts = [.. Conflicts],
        Resolved = [.. Resolved],
        AwaitingPull = [.. AwaitingPull]
    };
}
