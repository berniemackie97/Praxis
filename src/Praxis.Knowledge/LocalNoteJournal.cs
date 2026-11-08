namespace Praxis.Knowledge;

public sealed class LocalNoteJournal : IDisposable
{
    private readonly object gate = new();
    private readonly FileStream lease;
    private readonly string checkpointPath;
    private LocalJournalState state;
    private bool disposed;

    private LocalNoteJournal(FileStream lease, string checkpointPath, LocalJournalState state)
    {
        this.lease = lease;
        this.checkpointPath = checkpointPath;
        this.state = state;
    }

    public static LocalNoteJournal Open(string directory, Guid collectionEpoch, Guid deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (collectionEpoch == Guid.Empty || deviceId == Guid.Empty)
        {
            throw new ArgumentException("The collection epoch and device identity must be nonempty.");
        }

        var root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        var lease = new FileStream(Path.Combine(root, "journal.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            var checkpoint = Path.Combine(root, "journal.json");
            var exists = File.Exists(checkpoint);
            var state = exists
                ? LocalJournalCheckpoint.Read(checkpoint)
                : new LocalJournalState { CollectionEpoch = collectionEpoch, DeviceId = deviceId };
            if (state.CollectionEpoch != collectionEpoch || state.DeviceId != deviceId)
            {
                throw new InvalidDataException("The local journal belongs to a different collection epoch or device.");
            }

            if (!exists)
            {
                LocalJournalCheckpoint.Write(checkpoint, state);
            }

            return new LocalNoteJournal(lease, checkpoint, state);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public long Cursor
    {
        get { lock (gate) { EnsureOpen(); return state.Cursor; } }
    }

    public IReadOnlyList<LocalNoteState> Records
    {
        get { lock (gate) { EnsureOpen(); return Array.AsReadOnly(state.Records.ToArray()); } }
    }

    public IReadOnlyList<PendingNoteEdit> Pending
    {
        get { lock (gate) { EnsureOpen(); return Array.AsReadOnly(state.Pending.ToArray()); } }
    }

    public IReadOnlyList<RejectedNoteEdit> Conflicts
    {
        get { lock (gate) { EnsureOpen(); return Array.AsReadOnly(state.Conflicts.ToArray()); } }
    }

    public IReadOnlyList<AppliedNoteChange> AcknowledgedChanges
    {
        get { lock (gate) { EnsureOpen(); return Array.AsReadOnly(state.AwaitingPull.ToArray()); } }
    }

    public IReadOnlyList<RejectedNoteEdit> ResolvedConflicts
    {
        get { lock (gate) { EnsureOpen(); return Array.AsReadOnly(state.Resolved.ToArray()); } }
    }

    public IReadOnlyList<PendingNoteEdit> Outbound()
    {
        lock (gate)
        {
            EnsureOpen();
            var blocked = state.Conflicts.Select(item => item.Edit.RecordId).ToHashSet();
            var seen = new HashSet<Guid>();
            return Array.AsReadOnly(state.Pending.Where(item => seen.Add(item.RecordId) &&
                !blocked.Contains(item.RecordId) &&
                item.ExpectedRevision == (state.Records.FirstOrDefault(record =>
                    record.Id == item.RecordId)?.Revision ?? 0)).ToArray());
        }
    }

    public PendingNoteEdit Queue(Guid recordId, LocalNoteAction action,
        string? path = null, string? markdown = null)
    {
        lock (gate)
        {
            EnsureOpen();
            var candidate = state.Copy();
            var edit = Stage(candidate, recordId, action, path, markdown);
            Commit(candidate);
            return edit;
        }
    }

    public void Accept(Guid operationId, long acceptedSequence, LocalNoteState accepted)
    {
        lock (gate)
        {
            EnsureOpen();
            LocalJournalCheckpoint.ValidateRecord(accepted);
            if (acceptedSequence <= state.Cursor ||
                state.AwaitingPull.Any(change => change.Sequence == acceptedSequence))
            {
                throw new InvalidDataException("The accepted change sequence is invalid.");
            }

            var edit = FirstPending(operationId);
            var current = state.Records.FirstOrDefault(record => record.Id == edit.RecordId);
            if (state.Conflicts.Any(conflict => conflict.Edit.OperationId == operationId) ||
                edit.ExpectedRevision != (current?.Revision ?? 0))
            {
                throw new InvalidOperationException("The operation is blocked by an unresolved conflict.");
            }

            var expected = edit.Action == LocalNoteAction.Delete
                ? new LocalNoteState(edit.RecordId, edit.ExpectedRevision + 1,
                    current!.Path, current.Markdown, true)
                : new LocalNoteState(edit.RecordId, edit.ExpectedRevision + 1,
                    edit.Path!, edit.Markdown!, false);
            if (accepted != expected)
            {
                throw new InvalidDataException("The acknowledgment does not match the queued operation.");
            }

            var candidate = state.Copy();
            candidate.Records.RemoveAll(record => record.Id == accepted.Id);
            candidate.Records.Add(accepted);
            candidate.Pending.Remove(edit);
            candidate.AwaitingPull.Add(new AppliedNoteChange(acceptedSequence, accepted));
            Commit(candidate);
        }
    }

    public void Reject(Guid operationId, LocalNoteConflictReason reason, LocalNoteState? current)
    {
        lock (gate)
        {
            EnsureOpen();
            if (!Enum.IsDefined(reason))
            {
                throw new ArgumentOutOfRangeException(nameof(reason));
            }

            var edit = FirstPending(operationId);
            if (current is not null)
            {
                LocalJournalCheckpoint.ValidateRecord(current);
                if (current.Id != edit.RecordId)
                {
                    throw new InvalidDataException("The conflict refers to a different note.");
                }
            }

            var conflict = new RejectedNoteEdit(edit, reason, current);
            var previous = state.Conflicts.FirstOrDefault(item => item.Edit.OperationId == operationId);
            if (previous is not null)
            {
                if (previous != conflict)
                {
                    throw new InvalidDataException("The conflict response changed for the same operation.");
                }

                return;
            }

            var candidate = state.Copy();
            candidate.Conflicts.Add(conflict);
            Commit(candidate);
        }
    }

    public PendingNoteEdit Resolve(Guid conflictedOperationId, LocalNoteAction replacement,
        string? path = null, string? markdown = null)
    {
        lock (gate)
        {
            EnsureOpen();
            var conflict = state.Conflicts.SingleOrDefault(item =>
                item.Edit.OperationId == conflictedOperationId)
                ?? throw new InvalidOperationException("The operation has no recorded conflict.");
            var recordId = conflict.Edit.RecordId;
            if (state.Pending.Count(item => item.RecordId == recordId) != 1 ||
                state.Records.FirstOrDefault(record => record.Id == recordId) != conflict.Current)
            {
                throw new InvalidOperationException(
                    "Resolve dependent edits and refresh the authoritative record before rebasing.");
            }

            var candidate = state.Copy();
            candidate.Pending.Remove(conflict.Edit);
            candidate.Conflicts.Remove(conflict);
            candidate.Resolved.Add(conflict);
            var result = Stage(candidate, recordId, replacement, path, markdown);
            Commit(candidate);
            return result;
        }
    }

    public void ApplyPage(long expectedCursor, IReadOnlyList<AppliedNoteChange> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        lock (gate)
        {
            EnsureOpen();
            if (expectedCursor != state.Cursor || events.Count > 100)
            {
                throw new InvalidDataException("The remote page cursor or size is invalid.");
            }

            var candidate = state.Copy();
            foreach (var change in events)
            {
                if (change is null || change.Sequence != candidate.Cursor + 1)
                {
                    throw new InvalidDataException("The remote page contains a missing or reordered change.");
                }

                LocalJournalCheckpoint.ValidateRecord(change.Record);
                var acknowledged = candidate.AwaitingPull.FirstOrDefault(
                    receipt => receipt.Sequence == change.Sequence);
                if (acknowledged is not null)
                {
                    if (acknowledged.Record != change.Record)
                    {
                        throw new InvalidDataException("The echoed change differs from its accepted receipt.");
                    }

                    candidate.AwaitingPull.Remove(acknowledged);
                }
                else
                {
                    var existing = candidate.Records.FirstOrDefault(record =>
                        record.Id == change.Record.Id);
                    if (change.Record.Revision != (existing?.Revision ?? 0) + 1)
                    {
                        throw new InvalidDataException("The remote record revision is inconsistent.");
                    }

                    candidate.Records.RemoveAll(record => record.Id == change.Record.Id);
                    candidate.Records.Add(change.Record);
                }

                candidate.Cursor++;
            }

            AddStaleConflicts(candidate);
            Commit(candidate);
        }
    }

    public void Rebootstrap(LocalNoteSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        lock (gate)
        {
            EnsureOpen();
            if (snapshot.Records is null || snapshot.CollectionEpoch != state.CollectionEpoch ||
                snapshot.Cursor < 0)
            {
                throw new InvalidDataException("The snapshot uses an incompatible collection epoch or cursor.");
            }

            var candidate = state.Copy();
            candidate.Cursor = snapshot.Cursor;
            candidate.Records = snapshot.Records.ToList();
            candidate.AwaitingPull.RemoveAll(change => change.Sequence <= snapshot.Cursor);
            AddStaleConflicts(candidate);
            Commit(candidate);
        }
    }

    private PendingNoteEdit Stage(LocalJournalState candidate, Guid recordId,
        LocalNoteAction action, string? path, string? markdown)
    {
        if (recordId == Guid.Empty)
        {
            throw new ArgumentException("The record identity must be nonempty.", nameof(recordId));
        }

        var current = candidate.Records.FirstOrDefault(record => record.Id == recordId);
        var chain = candidate.Pending.Where(item => item.RecordId == recordId).ToArray();
        var deleted = chain.Length > 0
            ? chain[^1].Action == LocalNoteAction.Delete
            : current?.Deleted ?? false;
        var exists = chain.Length > 0 || current is not null;
        if (!exists && action != LocalNoteAction.Upsert ||
            exists && deleted && action != LocalNoteAction.Restore ||
            exists && !deleted && action != LocalNoteAction.Upsert && action != LocalNoteAction.Delete)
        {
            throw new InvalidOperationException("The requested edit does not match the local note lifecycle.");
        }

        var revision = chain.Length > 0
            ? checked(chain[^1].ExpectedRevision + 1)
            : current?.Revision ?? 0;
        var edit = new PendingNoteEdit(Guid.NewGuid(), recordId, revision, action, path, markdown);
        LocalJournalCheckpoint.ValidateEdit(edit);

        if (action != LocalNoteAction.Delete &&
            candidate.Records.Any(record => record.Id != recordId && !record.Deleted &&
                !candidate.Pending.Any(pending => pending.RecordId == record.Id &&
                    pending.Action == LocalNoteAction.Delete) &&
                string.Equals(record.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("An active note already uses that path.");
        }

        if (action != LocalNoteAction.Delete &&
            candidate.Pending.Where(pending => pending.RecordId != recordId)
                .GroupBy(pending => pending.RecordId)
                .Any(group => group.Last() is { Action: not LocalNoteAction.Delete } last &&
                    string.Equals(last.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Another pending note already uses that path.");
        }

        candidate.Pending.Add(edit);
        return edit;
    }

    private PendingNoteEdit FirstPending(Guid operationId)
    {
        var edit = state.Pending.FirstOrDefault(item => item.OperationId == operationId)
            ?? throw new InvalidOperationException("The operation is not queued.");
        if (state.Pending.First(item => item.RecordId == edit.RecordId) != edit)
        {
            throw new InvalidOperationException("Earlier edits for this note must be resolved first.");
        }

        return edit;
    }

    private static void AddStaleConflicts(LocalJournalState candidate)
    {
        foreach (var edit in candidate.Pending.GroupBy(item => item.RecordId).Select(group => group.First()))
        {
            var current = candidate.Records.FirstOrDefault(record => record.Id == edit.RecordId);
            if (edit.ExpectedRevision != (current?.Revision ?? 0) &&
                !candidate.Conflicts.Any(conflict => conflict.Edit.OperationId == edit.OperationId))
            {
                candidate.Conflicts.Add(new RejectedNoteEdit(
                    edit, LocalNoteConflictReason.StaleRevision, current));
            }
        }
    }

    private void Commit(LocalJournalState candidate)
    {
        LocalJournalCheckpoint.Write(checkpointPath, candidate);
        state = candidate;
    }

    private void EnsureOpen()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (!disposed)
            {
                disposed = true;
                lease.Dispose();
            }
        }
    }
}
