namespace Praxis.Knowledge.Tests.Synchronization;

internal enum SyncAction
{
    Upsert,
    Delete,
    Restore
}

internal enum ApplyStatus
{
    Applied,
    StaleRevision,
    PathCollision,
    UnsupportedProtocolVersion,
    WrongCollectionEpoch,
    InvalidRequest,
    OperationKeyReused
}

internal enum PullStatus
{
    Ok,
    WrongCollectionEpoch,
    CursorExpired,
    InvalidCursor
}

internal sealed record SyncMutation(
    int ProtocolVersion,
    Guid CollectionEpoch,
    Guid DeviceId,
    Guid OperationId,
    Guid RecordId,
    long BaseRevision,
    SyncAction Action,
    string? Path,
    string? Body,
    DateTimeOffset ClientTime);

internal sealed record SyncRecord(
    Guid Id, long Revision, string? Path, string? Body, bool Deleted);

internal sealed record SyncReply(
    ApplyStatus Status, long HeadSequence, SyncRecord? Current);

internal sealed record SyncChange(long Sequence, SyncRecord Record);

internal sealed record SyncPage(
    PullStatus Status, long Cursor, long HeadSequence, IReadOnlyList<SyncChange> Changes);

internal sealed record SyncSnapshot(
    Guid CollectionEpoch, long Cursor, IReadOnlyList<SyncRecord> Records);

/// <summary>Executable protocol specification; not a persistence or network implementation.</summary>
internal sealed class SyncContractFixture(Guid collectionEpoch)
{
    private readonly Dictionary<Guid, SyncRecord> records = [];
    private readonly Dictionary<(Guid DeviceId, Guid OperationId), (SyncMutation Request, SyncReply Reply)> receipts = [];
    private readonly List<SyncChange> changes = [];
    private long sequence;
    private long historyFloor;

    public Guid CollectionEpoch { get; } = collectionEpoch;

    public long HeadSequence => sequence;

    public SyncReply Apply(SyncMutation mutation)
    {
        if (mutation.CollectionEpoch != CollectionEpoch)
        {
            return Reject(ApplyStatus.WrongCollectionEpoch);
        }

        if (mutation.DeviceId == Guid.Empty || mutation.OperationId == Guid.Empty ||
            mutation.RecordId == Guid.Empty)
        {
            return Reject(ApplyStatus.InvalidRequest);
        }

        (Guid DeviceId, Guid OperationId) operationKey = (mutation.DeviceId, mutation.OperationId);
        if (receipts.TryGetValue(operationKey, out (SyncMutation Request, SyncReply Reply) previous))
        {
            return previous.Request == mutation
                ? previous.Reply
                : Reject(ApplyStatus.OperationKeyReused);
        }

        if (mutation.ProtocolVersion != 1)
        {
            return Reject(ApplyStatus.UnsupportedProtocolVersion);
        }

        if (!IsValid(mutation))
        {
            return Reject(ApplyStatus.InvalidRequest);
        }

        records.TryGetValue(mutation.RecordId, out SyncRecord? current);
        if (mutation.BaseRevision != (current?.Revision ?? 0))
        {
            return new SyncReply(ApplyStatus.StaleRevision, sequence, current);
        }

        if (current is null && mutation.Action != SyncAction.Upsert ||
            current?.Deleted == true && mutation.Action != SyncAction.Restore)
        {
            return new SyncReply(ApplyStatus.StaleRevision, sequence, current);
        }

        if (current is not null && !current.Deleted && mutation.Action == SyncAction.Restore)
        {
            return Reject(ApplyStatus.InvalidRequest);
        }

        if (mutation.Action != SyncAction.Delete &&
            records.Values.Any(record => record.Id != mutation.RecordId && !record.Deleted &&
                string.Equals(record.Path, mutation.Path, StringComparison.OrdinalIgnoreCase)))
        {
            return new SyncReply(ApplyStatus.PathCollision, sequence, current);
        }

        SyncRecord next = mutation.Action == SyncAction.Delete
            ? new SyncRecord(mutation.RecordId, current!.Revision + 1,
                current.Path, current.Body, true)
            : new SyncRecord(mutation.RecordId, (current?.Revision ?? 0) + 1,
                mutation.Path, mutation.Body, false);
        records[mutation.RecordId] = next;
        changes.Add(new SyncChange(++sequence, next));

        SyncReply reply = new(ApplyStatus.Applied, sequence, next);
        receipts.Add(operationKey, (mutation, reply));
        return reply;
    }

    public SyncPage Pull(Guid epoch, long cursor, int limit)
    {
        if (limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        if (epoch != CollectionEpoch)
        {
            return new SyncPage(PullStatus.WrongCollectionEpoch, cursor, sequence, []);
        }

        if (cursor < 0 || cursor > sequence)
        {
            return new SyncPage(PullStatus.InvalidCursor, cursor, sequence, []);
        }

        if (cursor < historyFloor)
        {
            return new SyncPage(PullStatus.CursorExpired, cursor, sequence, []);
        }

        SyncChange[] page = changes.Where(change => change.Sequence > cursor).Take(limit).ToArray();
        return new SyncPage(PullStatus.Ok,
            page.Length == 0 ? cursor : page[^1].Sequence, sequence, Array.AsReadOnly(page));
    }

    public SyncSnapshot Snapshot() => new(CollectionEpoch, sequence,
        Array.AsReadOnly(records.Values.OrderBy(record => record.Id).ToArray()));

    public void PruneThrough(long cursor)
    {
        if (cursor < historyFloor || cursor > sequence)
        {
            throw new ArgumentOutOfRangeException(nameof(cursor));
        }

        historyFloor = cursor;
        changes.RemoveAll(change => change.Sequence <= cursor);
    }

    private SyncReply Reject(ApplyStatus status) => new(status, sequence, null);

    private static bool IsValid(SyncMutation mutation)
    {
        if (mutation.BaseRevision < 0 || !Enum.IsDefined(mutation.Action))
        {
            return false;
        }

        if (mutation.Action == SyncAction.Delete)
        {
            return mutation.Path is null && mutation.Body is null;
        }

        return !string.IsNullOrEmpty(mutation.Path) && mutation.Body is not null;
    }
}
