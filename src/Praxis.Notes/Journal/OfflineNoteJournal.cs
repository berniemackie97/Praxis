namespace Praxis.Notes.Journal;

public sealed class OfflineNoteJournal : IDisposable
{
    private readonly object gate = new();
    private readonly FileStream lease;
    private readonly string checkpointPath;
    private LocalJournalState state;
    private bool disposed;

    private OfflineNoteJournal(FileStream lease, string checkpointPath, LocalJournalState state)
    {
        this.lease = lease;
        this.checkpointPath = checkpointPath;
        this.state = state;
    }

    public static OfflineNoteJournal Open(string directory, Guid collectionEpoch, Guid deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (collectionEpoch == Guid.Empty || deviceId == Guid.Empty)
        {
            throw new ArgumentException("The collection epoch and device identity must be nonempty.");
        }

        string root = Path.GetFullPath(directory);
        Directory.CreateDirectory(root);
        FileStream lease = new(Path.Combine(root, "journal.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        try
        {
            string checkpoint = Path.Combine(root, "journal.json");
            bool exists = File.Exists(checkpoint);
            LocalJournalState state = exists ? JournalCheckpoint.Read(checkpoint) : new LocalJournalState { CollectionEpoch = collectionEpoch, DeviceId = deviceId };
            if (state.CollectionEpoch != collectionEpoch || state.DeviceId != deviceId)
            {
                throw new InvalidDataException("The local journal belongs to a different collection epoch or device.");
            }

            if (!exists)
            {
                JournalCheckpoint.Write(checkpoint, state);
            }

            return new OfflineNoteJournal(lease, checkpoint, state);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public Guid CollectionEpoch
    {
        get { lock (gate) { EnsureOpen(); return state.CollectionEpoch; } }
    }

    public Guid DeviceId
    {
        get { lock (gate) { EnsureOpen(); return state.DeviceId; } }
    }

    public long Cursor
    {
        get
        {
            lock (gate)
            {
                EnsureOpen(); return state.Cursor;
            }
        }
    }

    public IReadOnlyList<LocalNoteState> Records
    {
        get
        {
            lock (gate)
            {
                EnsureOpen(); return Array.AsReadOnly(state.Records.ToArray());
            }
        }
    }

    public IReadOnlyList<PendingNoteEdit> Pending
    {
        get
        {
            lock (gate)
            {
                EnsureOpen(); return Array.AsReadOnly(state.Pending.ToArray());
            }
        }
    }

    public IReadOnlyList<IncomingNoteCollision> IncomingCollisions
    {
        get
        {
            lock (gate)
            {
                EnsureOpen(); return Array.AsReadOnly(state.IncomingCollisions.ToArray());
            }
        }
    }

    public IReadOnlyList<RejectedNoteEdit> Conflicts
    {
        get
        {
            lock (gate)
            {
                EnsureOpen(); return Array.AsReadOnly(state.Conflicts.ToArray());
            }
        }
    }

    public IReadOnlyList<AcceptedNoteReceipt> AcknowledgedChanges
    {
        get
        {
            lock (gate)
            {
                EnsureOpen(); return Array.AsReadOnly(state.AwaitingPull.ToArray());
            }
        }
    }

    public IReadOnlyList<RejectedNoteEdit> ResolvedConflicts
    {
        get
        {
            lock (gate)
            {
                EnsureOpen(); return Array.AsReadOnly(state.Resolved.ToArray());
            }
        }
    }

    public IReadOnlyList<PendingNoteEdit> Outbound()
    {
        lock (gate)
        {
            EnsureOpen();
            HashSet<Guid> blocked = state.Conflicts.Select(item => item.Edit.RecordId).ToHashSet();
            Dictionary<Guid, long> revisions = state.Records.ToDictionary(record => record.Id, record => record.Revision);
            foreach (IncomingNoteCollision collision in state.IncomingCollisions)
            {
                revisions[collision.Remote.Id] = collision.Remote.Revision;
            }
            HashSet<Guid> seen = [];
            return Array.AsReadOnly(state.Pending.Where(item => seen.Add(item.RecordId) && !blocked.Contains(item.RecordId)
                && item.ExpectedRevision == revisions.GetValueOrDefault(item.RecordId)).ToArray());
        }
    }

    public PendingNoteEdit Queue(Guid recordId, LocalNoteAction action, string? path = null, string? markdown = null)
    {
        lock (gate)
        {
            EnsureOpen();
            LocalJournalState candidate = state.Copy();
            PendingNoteEdit edit = Stage(candidate, recordId, action, path, markdown);
            Commit(candidate);
            return edit;
        }
    }

    public void Accept(Guid operationId, long acceptedSequence, LocalNoteState accepted)
    {
        lock (gate)
        {
            EnsureOpen();
            JournalCheckpoint.ValidateRecord(accepted);
            AcceptedNoteReceipt? previous = state.AwaitingPull.FirstOrDefault(receipt => receipt.OperationId == operationId);
            if (previous is not null)
            {
                if (previous.Sequence != acceptedSequence || previous.Record != accepted)
                {
                    throw new InvalidDataException("The replayed acknowledgement differs from its receipt.");
                }

                return;
            }

            if (acceptedSequence <= state.Cursor || state.AwaitingPull.Any(change => change.Sequence == acceptedSequence))
            {
                throw new InvalidDataException("The accepted change sequence is invalid.");
            }

            PendingNoteEdit edit = FirstPending(operationId);
            LocalNoteState? current = state.IncomingCollisions
                .FirstOrDefault(collision => collision.Remote.Id == edit.RecordId)?.Remote
                ?? state.Records.FirstOrDefault(record => record.Id == edit.RecordId);
            if (state.Conflicts.Any(conflict => conflict.Edit.OperationId == operationId) || edit.ExpectedRevision != (current?.Revision ?? 0))
            {
                throw new InvalidOperationException("The operation is blocked by an unresolved conflict.");
            }

            LocalNoteState expected = edit.Action == LocalNoteAction.Delete
                ? new LocalNoteState(edit.RecordId, edit.ExpectedRevision + 1, current!.Path, current.Markdown, true)
                : new LocalNoteState(edit.RecordId, edit.ExpectedRevision + 1, edit.Path!, edit.Markdown!, false);
            if (accepted != expected)
            {
                throw new InvalidDataException("The acknowledgment does not match the queued operation.");
            }

            LocalJournalState candidate = state.Copy();
            candidate.Records.RemoveAll(record => record.Id == accepted.Id);
            candidate.IncomingCollisions.RemoveAll(collision => collision.Remote.Id == accepted.Id);
            candidate.Records.Add(accepted);
            candidate.Pending.Remove(edit);
            candidate.AwaitingPull.Add(new AcceptedNoteReceipt(operationId, acceptedSequence, accepted));
            ReleaseUnblockedIncoming(candidate);
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

            PendingNoteEdit edit = FirstPending(operationId);
            if (current is not null)
            {
                JournalCheckpoint.ValidateRecord(current);
                if (current.Id != edit.RecordId)
                {
                    throw new InvalidDataException("The conflict refers to a different note.");
                }
            }

            RejectedNoteEdit conflict = new(edit, reason, current);
            RejectedNoteEdit? previous = state.Conflicts.FirstOrDefault(item => item.Edit.OperationId == operationId);
            if (previous is not null)
            {
                if (previous != conflict)
                {
                    throw new InvalidDataException("The conflict response changed for the same operation.");
                }

                return;
            }

            LocalJournalState candidate = state.Copy();
            candidate.Conflicts.Add(conflict);
            Commit(candidate);
        }
    }

    public PendingNoteEdit Resolve(Guid conflictedOperationId, LocalNoteAction replacement, string? path = null, string? markdown = null)
    {
        lock (gate)
        {
            EnsureOpen();
            RejectedNoteEdit conflict = state.Conflicts.SingleOrDefault(item => item.Edit.OperationId == conflictedOperationId)
                                        ?? throw new InvalidOperationException("The operation has no recorded conflict.");
            Guid recordId = conflict.Edit.RecordId;
            IncomingNoteCollision? incoming = state.IncomingCollisions
                .FirstOrDefault(item => item.Remote.Id == recordId);
            LocalNoteState? current = incoming?.Remote ??
                state.Records.FirstOrDefault(record => record.Id == recordId);
            if (state.Pending.Count(item => item.RecordId == recordId) != 1 ||
                current != conflict.Current)
            {
                throw new InvalidOperationException("Resolve dependent edits and refresh the authoritative record before rebasing.");
            }

            LocalJournalState candidate = state.Copy();
            candidate.Pending.Remove(conflict.Edit);
            candidate.Conflicts.Remove(conflict);
            candidate.Resolved.Add(conflict);
            PendingNoteEdit result = incoming is null
                ? Stage(candidate, recordId, replacement, path, markdown)
                : StageIncomingResolution(candidate, incoming, replacement, path, markdown);
            ReleaseUnblockedIncoming(candidate);
            Commit(candidate);
            return result;
        }
    }

    public PendingNoteEdit ResolveIncoming(Guid recordId, LocalNoteAction action, string? path = null)
    {
        lock (gate)
        {
            EnsureOpen();
            IncomingNoteCollision collision = state.IncomingCollisions
                .SingleOrDefault(item => item.Remote.Id == recordId)
                ?? throw new InvalidOperationException("The record has no unresolved incoming collision.");
            if (state.Pending.Any(edit => edit.RecordId == recordId) ||
                state.Conflicts.Any(conflict => conflict.Edit.RecordId == recordId))
            {
                throw new InvalidOperationException("Resolve existing edits for this record first.");
            }

            LocalJournalState candidate = state.Copy();
            PendingNoteEdit resolution = StageIncomingResolution(candidate, collision,
                action, path, collision.Remote.Markdown);
            Commit(candidate);
            return resolution;
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

            LocalJournalState candidate = state.Copy();
            foreach (AppliedNoteChange? change in events)
            {
                if (change is null || change.Sequence != candidate.Cursor + 1)
                {
                    throw new InvalidDataException("The remote page contains a missing or reordered change.");
                }

                JournalCheckpoint.ValidateRecord(change.Record);
                AcceptedNoteReceipt? acknowledged = candidate.AwaitingPull.FirstOrDefault(receipt => receipt.Sequence == change.Sequence);
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
                    IncomingNoteCollision? incoming = candidate.IncomingCollisions
                        .FirstOrDefault(item => item.Remote.Id == change.Record.Id);
                    LocalNoteState? existing = incoming?.Remote ??
                        candidate.Records.FirstOrDefault(record => record.Id == change.Record.Id);
                    if (change.Record.Revision != (existing?.Revision ?? 0) + 1)
                    {
                        throw new InvalidDataException("The remote record revision is inconsistent.");
                    }

                    Guid blocker = FindPathBlocker(candidate, change.Record);
                    if (blocker != Guid.Empty)
                    {
                        candidate.IncomingCollisions.RemoveAll(item => item.Remote.Id == change.Record.Id);
                        candidate.IncomingCollisions.Add(new IncomingNoteCollision(change.Record, blocker));
                    }
                    else if (incoming is not null &&
                             candidate.Pending.Any(edit => edit.RecordId == change.Record.Id))
                    {
                        candidate.IncomingCollisions.RemoveAll(item => item.Remote.Id == change.Record.Id);
                        candidate.IncomingCollisions.Add(incoming with { Remote = change.Record });
                    }
                    else
                    {
                        candidate.IncomingCollisions.RemoveAll(item => item.Remote.Id == change.Record.Id);
                        candidate.Records.RemoveAll(record => record.Id == change.Record.Id);
                        candidate.Records.Add(change.Record);
                    }
                }

                candidate.Cursor++;
            }

            ReleaseUnblockedIncoming(candidate);
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
            if (snapshot.Records is null || snapshot.CollectionEpoch != state.CollectionEpoch || snapshot.Cursor < 0)
            {
                throw new InvalidDataException("The snapshot uses an incompatible collection epoch or cursor.");
            }

            LocalJournalState candidate = state.Copy();
            candidate.Cursor = snapshot.Cursor;
            candidate.Records = snapshot.Records.ToList();
            candidate.AwaitingPull.RemoveAll(change => change.Sequence <= snapshot.Cursor);
            AddStaleConflicts(candidate);
            Commit(candidate);
        }
    }

    private PendingNoteEdit Stage(LocalJournalState candidate, Guid recordId, LocalNoteAction action, string? path, string? markdown)
    {
        if (candidate.IncomingCollisions.Any(collision => collision.Remote.Id == recordId))
        {
            throw new InvalidOperationException("Resolve the incoming note collision before editing this record.");
        }

        if (recordId == Guid.Empty)
        {
            throw new ArgumentException("The record identity must be nonempty.", nameof(recordId));
        }

        LocalNoteState? current = candidate.Records.FirstOrDefault(record => record.Id == recordId);
        PendingNoteEdit[] chain = candidate.Pending.Where(item => item.RecordId == recordId).ToArray();
        bool deleted = chain.Length > 0 ? chain[^1].Action == LocalNoteAction.Delete : current?.Deleted ?? false;
        bool exists = chain.Length > 0 || current is not null;
        if (!exists && action != LocalNoteAction.Upsert || exists && deleted && action != LocalNoteAction.Restore
                                                        || exists && !deleted && action != LocalNoteAction.Upsert && action != LocalNoteAction.Delete)
        {
            throw new InvalidOperationException("The requested edit does not match the local note lifecycle.");
        }

        long revision = chain.Length > 0 ? checked(chain[^1].ExpectedRevision + 1) : current?.Revision ?? 0;
        PendingNoteEdit edit = new(Guid.NewGuid(), recordId, revision, action, path, markdown);
        JournalCheckpoint.ValidateEdit(edit);

        if (action != LocalNoteAction.Delete && candidate.Records.Any(record => record.Id != recordId && !record.Deleted
                && !candidate.Pending.Any(pending => pending.RecordId == record.Id && pending.Action == LocalNoteAction.Delete)
                && string.Equals(record.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("An active note already uses that path.");
        }

        if (action != LocalNoteAction.Delete && candidate.Pending.Where(pending => pending.RecordId != recordId)
                .GroupBy(pending => pending.RecordId)
                .Any(group => group.Last() is { Action: not LocalNoteAction.Delete } last && string.Equals(last.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Another pending note already uses that path.");
        }

        candidate.Pending.Add(edit);
        return edit;
    }

    private static Guid FindPathBlocker(LocalJournalState candidate, LocalNoteState incoming,
        bool includeIncoming = true)
    {
        if (incoming.Deleted)
        {
            return Guid.Empty;
        }

        LocalNoteState? active = candidate.Records.FirstOrDefault(record =>
            record.Id != incoming.Id && !record.Deleted &&
            string.Equals(record.Path, incoming.Path, StringComparison.OrdinalIgnoreCase));
        if (active is not null)
        {
            return active.Id;
        }

        PendingNoteEdit? reserved = candidate.Pending
            .Where(edit => edit.RecordId != incoming.Id)
            .GroupBy(edit => edit.RecordId)
            .Select(group => group.Last())
            .FirstOrDefault(edit => edit.Action != LocalNoteAction.Delete &&
                string.Equals(edit.Path, incoming.Path, StringComparison.OrdinalIgnoreCase));
        if (reserved is not null)
        {
            return reserved.RecordId;
        }

        return includeIncoming
            ? candidate.IncomingCollisions.FirstOrDefault(collision =>
                collision.Remote.Id != incoming.Id && !collision.Remote.Deleted &&
                string.Equals(collision.Remote.Path, incoming.Path, StringComparison.OrdinalIgnoreCase))
                ?.Remote.Id ?? Guid.Empty
            : Guid.Empty;
    }

    private static void ReleaseUnblockedIncoming(LocalJournalState candidate)
    {
        foreach (IncomingNoteCollision collision in candidate.IncomingCollisions.ToArray())
        {
            if (candidate.Pending.Any(edit => edit.RecordId == collision.Remote.Id))
            {
                continue;
            }

            Guid blocker = FindPathBlocker(candidate, collision.Remote, includeIncoming: false);
            if (blocker != Guid.Empty)
            {
                if (blocker != collision.BlockingRecordId)
                {
                    int index = candidate.IncomingCollisions.IndexOf(collision);
                    candidate.IncomingCollisions[index] = collision with { BlockingRecordId = blocker };
                }

                continue;
            }

            candidate.IncomingCollisions.Remove(collision);
            candidate.Records.RemoveAll(record => record.Id == collision.Remote.Id);
            candidate.Records.Add(collision.Remote);
        }
    }

    private static PendingNoteEdit StageIncomingResolution(LocalJournalState candidate,
        IncomingNoteCollision collision, LocalNoteAction action, string? path, string? markdown)
    {
        if (collision.Remote.Deleted
            ? action != LocalNoteAction.Restore
            : action is not (LocalNoteAction.Upsert or LocalNoteAction.Delete))
        {
            throw new InvalidOperationException("The resolution does not match the incoming note lifecycle.");
        }

        PendingNoteEdit resolution = new(Guid.NewGuid(), collision.Remote.Id,
            collision.Remote.Revision, action,
            action == LocalNoteAction.Delete ? null : path,
            action == LocalNoteAction.Delete ? null : markdown);
        JournalCheckpoint.ValidateEdit(resolution);
        if (action != LocalNoteAction.Delete &&
            FindPathBlocker(candidate, collision.Remote with { Path = path! }) != Guid.Empty)
        {
            throw new InvalidOperationException("The resolved note path is already reserved.");
        }

        candidate.Pending.Add(resolution);
        return resolution;
    }

    private PendingNoteEdit FirstPending(Guid operationId)
    {
        PendingNoteEdit edit = state.Pending.FirstOrDefault(item => item.OperationId == operationId) ?? throw new InvalidOperationException("The operation is not queued.");
        return state.Pending.First(item => item.RecordId == edit.RecordId) != edit
            ? throw new InvalidOperationException("Earlier edits for this note must be resolved first.")
            : edit;
    }

    private static void AddStaleConflicts(LocalJournalState candidate)
    {
        foreach (PendingNoteEdit edit in candidate.Pending.GroupBy(item => item.RecordId).Select(group => group.First()))
        {
            LocalNoteState? current = candidate.IncomingCollisions
                .FirstOrDefault(collision => collision.Remote.Id == edit.RecordId)?.Remote
                ?? candidate.Records.FirstOrDefault(record => record.Id == edit.RecordId);
            if (edit.ExpectedRevision != (current?.Revision ?? 0))
            {
                int index = candidate.Conflicts.FindIndex(conflict => conflict.Edit.OperationId == edit.OperationId);
                if (index < 0)
                {
                    candidate.Conflicts.Add(new RejectedNoteEdit(edit, LocalNoteConflictReason.StaleRevision, current));
                }
                else if (candidate.Conflicts[index] is { Reason: LocalNoteConflictReason.StaleRevision } conflict &&
                         conflict.Current != current)
                {
                    candidate.Conflicts[index] = conflict with { Current = current };
                }
            }
        }
    }

    private void Commit(LocalJournalState candidate)
    {
        JournalCheckpoint.Write(checkpointPath, candidate);
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
            if (disposed)
            {
                return;
            }

            disposed = true;
            lease.Dispose();
        }
    }
}
