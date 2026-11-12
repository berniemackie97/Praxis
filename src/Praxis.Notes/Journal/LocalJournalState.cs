namespace Praxis.Notes.Journal;

internal sealed class LocalJournalState
{
    public Guid CollectionEpoch { get; set; }
    public Guid DeviceId { get; set; }
    public long Cursor { get; set; }
    public List<LocalNoteState> Records { get; set; } = [];
    public List<PendingNoteEdit> Pending { get; set; } = [];
    public List<RejectedNoteEdit> Conflicts { get; set; } = [];
    public List<IncomingNoteCollision> IncomingCollisions { get; set; } = [];
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
        IncomingCollisions = [.. IncomingCollisions],
        Resolved = [.. Resolved],
        AwaitingPull = [.. AwaitingPull]
    };
}
