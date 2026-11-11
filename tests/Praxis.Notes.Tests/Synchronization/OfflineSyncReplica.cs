namespace Praxis.Notes.Tests.Synchronization;

internal sealed record SyncConflict(SyncMutation Mutation, SyncReply Reply);

internal sealed record ReplicaCheckpoint(
    Guid CollectionEpoch,
    Guid DeviceId,
    long Cursor,
    IReadOnlyList<SyncRecord> Records,
    IReadOnlyList<SyncMutation> Pending,
    IReadOnlyList<SyncConflict> Conflicts);

/// <summary>Simulated client checkpoint and outbox; no filesystem or transport durability.</summary>
internal sealed class OfflineSyncReplica
{
    private Dictionary<Guid, SyncRecord> records;
    private readonly List<SyncMutation> pending;
    private readonly List<SyncConflict> conflicts;

    private OfflineSyncReplica(
        Guid collectionEpoch, Guid deviceId, long cursor,
        Dictionary<Guid, SyncRecord> records, List<SyncMutation> pending,
        List<SyncConflict> conflicts)
    {
        CollectionEpoch = collectionEpoch;
        DeviceId = deviceId;
        Cursor = cursor;
        this.records = records;
        this.pending = pending;
        this.conflicts = conflicts;
    }

    public Guid CollectionEpoch { get; }

    public Guid DeviceId { get; }

    public long Cursor { get; private set; }

    public IReadOnlyList<SyncMutation> Pending => pending.AsReadOnly();

    public IReadOnlyList<SyncConflict> Conflicts => conflicts.AsReadOnly();

    public static OfflineSyncReplica Start(Guid collectionEpoch, Guid deviceId)
        => new(collectionEpoch, deviceId, 0, [], [], []);

    public static OfflineSyncReplica Restart(ReplicaCheckpoint checkpoint)
        => new(checkpoint.CollectionEpoch, checkpoint.DeviceId, checkpoint.Cursor,
            checkpoint.Records.ToDictionary(record => record.Id),
            checkpoint.Pending.ToList(), checkpoint.Conflicts.ToList());

    public ReplicaCheckpoint Checkpoint()
        => new(CollectionEpoch, DeviceId, Cursor,
            Array.AsReadOnly(records.Values.OrderBy(record => record.Id).ToArray()),
            Array.AsReadOnly(pending.ToArray()), Array.AsReadOnly(conflicts.ToArray()));

    public void Queue(SyncMutation mutation)
    {
        if (mutation.CollectionEpoch != CollectionEpoch || mutation.DeviceId != DeviceId)
        {
            throw new ArgumentException("An outbound operation must belong to this replica.", nameof(mutation));
        }

        if (pending.Any(existing => existing.OperationId == mutation.OperationId))
        {
            throw new ArgumentException("An outbound operation identity is already queued.", nameof(mutation));
        }

        pending.Add(mutation);
    }

    public void Acknowledge(SyncMutation mutation, SyncReply reply)
    {
        if (!pending.Contains(mutation))
        {
            throw new InvalidOperationException("The response does not belong to a queued operation.");
        }

        if (reply.Status == ApplyStatus.Applied)
        {
            if (reply.Current?.Id != mutation.RecordId ||
                reply.Current.Revision != mutation.BaseRevision + 1)
            {
                throw new InvalidDataException("An accepted receipt does not match its operation.");
            }

            pending.Remove(mutation);
        }
        else if (reply.Status is ApplyStatus.StaleRevision or ApplyStatus.PathCollision)
        {
            pending.Remove(mutation);
            conflicts.Add(new SyncConflict(mutation, reply));
        }
        else
        {
            throw new InvalidOperationException("An invalid or unauthorized request needs explicit handling.");
        }
    }

    public void CommitPage(SyncPage page)
    {
        if (page.Status != PullStatus.Ok)
        {
            throw new InvalidDataException("A failed page cannot be applied.");
        }

        if (page.Cursor < 0 || page.HeadSequence < page.Cursor)
        {
            throw new InvalidDataException("The page contains invalid cursor metadata.");
        }

        long lastSequence = page.Changes.Count == 0 ? page.Cursor : page.Changes[^1].Sequence;
        if (page.Cursor != lastSequence)
        {
            throw new InvalidDataException("The page cursor does not match its final event.");
        }

        if (page.Changes.Count == 0 && page.Cursor > Cursor)
        {
            throw new InvalidDataException("An empty page cannot skip unapplied events.");
        }

        Dictionary<Guid, SyncRecord> staged = new(records);
        long appliedCursor = Cursor;
        long? previous = null;
        foreach (SyncChange change in page.Changes)
        {
            if (previous.HasValue && change.Sequence != previous + 1)
            {
                throw new InvalidDataException("The page contains missing or reordered events.");
            }

            previous = change.Sequence;

            if (change.Sequence <= Cursor)
            {
                continue;
            }

            if (change.Sequence != appliedCursor + 1)
            {
                throw new InvalidDataException("An event cannot be applied before its predecessor.");
            }

            staged.TryGetValue(change.Record.Id, out SyncRecord? current);
            if (change.Record.Revision != (current?.Revision ?? 0) + 1)
            {
                throw new InvalidDataException("The resulting record revision is inconsistent.");
            }

            staged[change.Record.Id] = change.Record;
            appliedCursor = change.Sequence;
        }

        records = staged;
        Cursor = appliedCursor;
    }

    public void Rebootstrap(SyncSnapshot snapshot)
    {
        if (snapshot.CollectionEpoch != CollectionEpoch || snapshot.Cursor < 0)
        {
            throw new InvalidDataException("The snapshot does not match the collection generation.");
        }

        Dictionary<Guid, SyncRecord> restored = new();
        foreach (SyncRecord record in snapshot.Records)
        {
            if (record.Revision <= 0 || !restored.TryAdd(record.Id, record))
            {
                throw new InvalidDataException("The snapshot contains invalid or duplicate records.");
            }
        }

        records = restored;
        Cursor = snapshot.Cursor;
    }
}
