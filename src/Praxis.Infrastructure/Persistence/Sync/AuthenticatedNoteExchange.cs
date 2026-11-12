using System.Security.Cryptography;
using Praxis.Infrastructure.Security.Cryptography;
using Praxis.Notes.Journal;
using Praxis.Notes.Synchronization;

namespace Praxis.Infrastructure.Persistence.Sync;

public sealed class AuthenticatedNoteExchange : INoteSyncExchange, IDisposable
{
    private const int ProtocolVersion = 1;
    private readonly OfflineNoteJournal journal;
    private readonly Guid spaceId;
    private readonly long contentEpoch;
    private readonly byte[] contentKey;
    private readonly OpaqueSyncTransportClient transport;
    private readonly SealedNoteOutbox outbox;
    private bool disposed;

    public AuthenticatedNoteExchange(OfflineNoteJournal journal, Guid spaceId,
        long contentEpoch, byte[] contentKey, OpaqueSyncTransportClient transport,
        string outboxDirectory)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(contentKey);
        ArgumentNullException.ThrowIfNull(transport);
        if (spaceId == Guid.Empty || contentEpoch < 1 || contentKey.Length != 32)
        {
            throw new ArgumentException("A valid security space and client-held key are required.");
        }

        this.journal = journal;
        this.spaceId = spaceId;
        this.contentEpoch = contentEpoch;
        this.contentKey = contentKey.ToArray();
        this.transport = transport;
        outbox = new SealedNoteOutbox(outboxDirectory, spaceId,
            journal.CollectionEpoch, journal.DeviceId, contentEpoch);
        outbox.Prune(journal.Pending);
    }

    public async Task<NoteSubmitResult> SubmitAsync(NoteSubmitRequest request,
        CancellationToken cancellationToken)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(request);
        CheckScope(request.ProtocolVersion, request.SecuritySpaceId,
            request.CollectionEpoch, request.DeviceId);
        ArgumentNullException.ThrowIfNull(request.Edit);
        if (!journal.Pending.Contains(request.Edit))
        {
            throw new InvalidOperationException("Only a durably queued note edit can be sent.");
        }

        OpaqueSyncMutation mutation = outbox.GetOrSeal(request.Edit, spaceId,
            journal.CollectionEpoch, journal.DeviceId, contentEpoch, contentKey,
            journal.IncomingCollisions.Select(collision => collision.Remote)
                .Concat(journal.Records).ToArray());
        OpaqueSyncReceipt receipt = await transport.SubmitAsync(mutation, cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (receipt.SpaceId != spaceId || receipt.CollectionEpoch != journal.CollectionEpoch ||
            receipt.DeviceId != journal.DeviceId || receipt.OperationId != request.Edit.OperationId)
        {
            throw new InvalidDataException("The authenticated receipt does not match the request.");
        }

        NoteSubmitStatus status = receipt.Status switch
        {
            OpaqueSyncStatus.Applied => NoteSubmitStatus.Applied,
            OpaqueSyncStatus.StaleRevision => NoteSubmitStatus.StaleRevision,
            OpaqueSyncStatus.WrongCollectionEpoch => NoteSubmitStatus.WrongCollectionEpoch,
            OpaqueSyncStatus.UnsupportedProtocolVersion => NoteSubmitStatus.UnsupportedProtocolVersion,
            OpaqueSyncStatus.OperationKeyReused => NoteSubmitStatus.OperationKeyReused,
            OpaqueSyncStatus.ResetRequired => NoteSubmitStatus.ResetRequired,
            OpaqueSyncStatus.QuotaExceeded => NoteSubmitStatus.QuotaExceeded,
            _ => NoteSubmitStatus.InvalidRequest
        };
        LocalNoteState? accepted = null;
        long sequence = 0;
        if (status == NoteSubmitStatus.Applied)
        {
            if (receipt.Sequence <= journal.Cursor ||
                receipt.Revision != request.Edit.ExpectedRevision + 1)
            {
                throw new InvalidDataException("The encrypted receipt has an invalid revision or sequence.");
            }

            accepted = AcceptedRecord(request.Edit);
            sequence = receipt.Sequence;
        }

        return new NoteSubmitResult(ProtocolVersion, status, spaceId,
            journal.CollectionEpoch, journal.DeviceId, request.Edit.OperationId, sequence, accepted);
    }

    public async Task<NotePullResult> PullAsync(NotePullRequest request,
        CancellationToken cancellationToken)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(request);
        CheckScope(request.ProtocolVersion, request.SecuritySpaceId,
            request.CollectionEpoch, request.DeviceId);
        if (request.AfterSequence < 0 || request.Limit is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        outbox.Prune(journal.Pending);
        OpaqueSyncPage page = await transport.PullAsync(new OpaqueSyncPull(
            ProtocolVersion, spaceId, journal.CollectionEpoch, journal.DeviceId,
            contentEpoch, request.AfterSequence, request.Limit), cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (page.SpaceId != spaceId || page.CollectionEpoch != journal.CollectionEpoch ||
            page.DeviceId != journal.DeviceId || page.RequestedCursor != request.AfterSequence ||
            page.Changes is null || page.Changes.Count > request.Limit)
        {
            throw new InvalidDataException("The encrypted synchronization page has an invalid scope.");
        }

        NotePullStatus status = page.Status switch
        {
            OpaqueSyncStatus.Ok => NotePullStatus.Ok,
            OpaqueSyncStatus.CursorExpired => NotePullStatus.CursorExpired,
            OpaqueSyncStatus.InvalidCursor => NotePullStatus.InvalidCursor,
            OpaqueSyncStatus.WrongCollectionEpoch => NotePullStatus.WrongCollectionEpoch,
            OpaqueSyncStatus.UnsupportedProtocolVersion => NotePullStatus.UnsupportedProtocolVersion,
            OpaqueSyncStatus.ResetRequired => NotePullStatus.ResetRequired,
            _ => throw new InvalidDataException("The encrypted endpoint rejected the pull contract.")
        };

        if (status != NotePullStatus.Ok)
        {
            if (page.Changes.Count != 0 || page.NextCursor != request.AfterSequence ||
                page.HeadSequence != 0)
            {
                throw new InvalidDataException("A rejected encrypted page disclosed private state.");
            }

            return new NotePullResult(ProtocolVersion, status, spaceId,
                journal.CollectionEpoch, journal.DeviceId, request.AfterSequence,
                request.AfterSequence, 0, []);
        }

        if (page.HeadSequence < request.AfterSequence || page.NextCursor < request.AfterSequence ||
            page.NextCursor > page.HeadSequence)
        {
            throw new InvalidDataException("The encrypted page cursor is invalid.");
        }

        Dictionary<Guid, LocalNoteState> projected = journal.Records.ToDictionary(note => note.Id);
        foreach (IncomingNoteCollision collision in journal.IncomingCollisions)
        {
            projected[collision.Remote.Id] = collision.Remote;
        }

        Dictionary<long, AcceptedNoteReceipt> acknowledged = journal.AcknowledgedChanges
            .ToDictionary(receipt => receipt.Sequence);
        List<AppliedNoteChange> changes = new(page.Changes.Count);
        long sequence = request.AfterSequence;
        foreach (OpaqueSyncEvent change in page.Changes)
        {
            if (change.Sequence != ++sequence || change.RecordId == Guid.Empty ||
                change.Revision <= 0 || change.ContentEpoch != contentEpoch ||
                change.Ciphertext is null)
            {
                throw new InvalidDataException("The encrypted event sequence or scope is invalid.");
            }

            projected.TryGetValue(change.RecordId, out LocalNoteState? prior);
            acknowledged.TryGetValue(change.Sequence, out AcceptedNoteReceipt? accepted);
            if (accepted is not null && (accepted.Record.Id != change.RecordId ||
                                         accepted.Record.Revision != change.Revision))
            {
                throw new InvalidDataException("An encrypted event contradicts a durable receipt.");
            }

            LocalNoteState next = Decode(change, prior, accepted);
            if (accepted is null && change.Revision != (prior?.Revision ?? 0) + 1 ||
                accepted is not null && next != accepted.Record)
            {
                throw new InvalidDataException("The encrypted event contradicts local revision history.");
            }

            projected[change.RecordId] = next;
            changes.Add(new AppliedNoteChange(change.Sequence, next));
        }

        if (page.NextCursor != sequence)
        {
            throw new InvalidDataException("The encrypted page omitted a committed event.");
        }

        return new NotePullResult(ProtocolVersion, status, spaceId,
            journal.CollectionEpoch, journal.DeviceId, request.AfterSequence,
            page.NextCursor, page.HeadSequence, changes.AsReadOnly());
    }

    public Task<NoteSnapshotResult> SnapshotAsync(Guid securitySpaceId,
        Guid collectionEpoch, Guid deviceId, CancellationToken cancellationToken)
    {
        EnsureOpen();
        CheckScope(ProtocolVersion, securitySpaceId, collectionEpoch, deviceId);
        throw new NotSupportedException(
            "An authenticated, consistent encrypted reset snapshot is not available; the local journal remains intact.");
    }

    private LocalNoteState Decode(OpaqueSyncEvent change, LocalNoteState? prior,
        AcceptedNoteReceipt? acknowledged)
    {
        if (change.Deleted && change.Ciphertext.Length == 0)
        {
            throw new InvalidDataException("Legacy unauthenticated deletions cannot enter a client-held note journal.");
        }

        OpaqueSyncAction action = change.Deleted
            ? OpaqueSyncAction.Delete
            : prior?.Deleted == true ? OpaqueSyncAction.Restore : OpaqueSyncAction.Upsert;
        OpaqueNoteContent note;
        if (acknowledged is not null && !change.Deleted)
        {
            try
            {
                note = ClientOpaqueNoteCipher.Decrypt(contentKey, spaceId,
                    journal.CollectionEpoch, change.RecordId, change.Revision,
                    action, contentEpoch, change.Ciphertext);
            }
            catch (CryptographicException) when (action == OpaqueSyncAction.Upsert)
            {
                note = ClientOpaqueNoteCipher.Decrypt(contentKey, spaceId,
                    journal.CollectionEpoch, change.RecordId, change.Revision,
                    OpaqueSyncAction.Restore, contentEpoch, change.Ciphertext);
            }
        }
        else
        {
            note = ClientOpaqueNoteCipher.Decrypt(contentKey, spaceId,
                journal.CollectionEpoch, change.RecordId, change.Revision, action,
                contentEpoch, change.Ciphertext);
        }

        if (change.Deleted && acknowledged is null &&
            (prior is null || prior.Deleted || prior.Path != note.Path ||
             prior.Markdown != note.Markdown))
        {
            throw new InvalidDataException("The encrypted deletion does not match its prior note.");
        }

        return new LocalNoteState(change.RecordId, change.Revision, note.Path,
            note.Markdown, change.Deleted);
    }

    private LocalNoteState AcceptedRecord(PendingNoteEdit edit)
    {
        LocalNoteState? existing = journal.IncomingCollisions
            .FirstOrDefault(collision => collision.Remote.Id == edit.RecordId)?.Remote
            ?? journal.Records.FirstOrDefault(note => note.Id == edit.RecordId);
        return edit.Action == LocalNoteAction.Delete
            ? existing is { Deleted: false }
                ? existing with { Revision = edit.ExpectedRevision + 1, Deleted = true }
                : throw new InvalidDataException("A deleted note must have a verified local predecessor.")
            : new LocalNoteState(edit.RecordId, edit.ExpectedRevision + 1,
                edit.Path!, edit.Markdown!, false);
    }

    private void CheckScope(int version, Guid requestedSpace, Guid requestedEpoch, Guid device)
    {
        if (version != ProtocolVersion || requestedSpace != spaceId ||
            requestedEpoch != journal.CollectionEpoch || device != journal.DeviceId)
        {
            throw new InvalidDataException("The local synchronization request uses a foreign scope.");
        }
    }

    private void EnsureOpen() => ObjectDisposedException.ThrowIf(disposed, this);

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            CryptographicOperations.ZeroMemory(contentKey);
        }
    }
}
