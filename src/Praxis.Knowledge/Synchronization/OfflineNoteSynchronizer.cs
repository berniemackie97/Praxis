using Praxis.Knowledge.Journal;

namespace Praxis.Knowledge.Synchronization;

/// <summary>
/// Reconciles one durable local journal with a separately authenticated sync
/// exchange. The exchange must return client-verified projections, never an
/// unauthenticated server's plaintext claims or an unencrypted wire payload.
/// </summary>
public sealed class OfflineNoteSynchronizer(LocalNoteJournal journal,
    Guid securitySpaceId, INoteSyncExchange exchange)
{
    private const int ProtocolVersion = 1;
    private const int PageLimit = 100;
    private const int MaximumOperationsPerCycle = 128;
    private const int MaximumPagesPerCycle = 32;
    private readonly SemaphoreSlim cycle = new(1, 1);

    public async Task<NoteSyncProgress> ReconcileAsync(CancellationToken cancellationToken = default)
    {
        if (securitySpaceId == Guid.Empty)
        {
            throw new ArgumentException("A security-space identity is required.", nameof(securitySpaceId));
        }

        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(exchange);
        await cycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int accepted = 0;
            int beforeConflicts = journal.Conflicts.Count;
            int received = 0;
            bool snapshotApplied = false;
            Guid epoch = journal.CollectionEpoch;
            Guid deviceId = journal.DeviceId;

            // Retry the durable outbox before reading newer remote projections:
            // a lost acknowledgment must not convert an accepted edit into a conflict.
            while (accepted + journal.Conflicts.Count - beforeConflicts < MaximumOperationsPerCycle)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PendingNoteEdit? edit = journal.Outbound().FirstOrDefault();
                if (edit is null)
                {
                    break;
                }

                NoteSubmitRequest request = new(ProtocolVersion, securitySpaceId, epoch,
                    deviceId, edit);
                NoteSubmitResult response = await exchange.SubmitAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (response is null || response.SecuritySpaceId != securitySpaceId ||
                    response.DeviceId != deviceId || response.OperationId != edit.OperationId)
                {
                    throw new InvalidDataException("The synchronization receipt is not bound to the submitted operation.");
                }

                if (response.Status == NoteSubmitStatus.WrongCollectionEpoch)
                {
                    RequireNoPrivateResponse(response);
                    return Progress(NoteSyncState.EpochChanged);
                }

                if (response.CollectionEpoch != epoch)
                {
                    throw new InvalidDataException("The receipt uses a foreign collection epoch.");
                }

                switch (response.Status)
                {
                    case NoteSubmitStatus.Applied:
                        if (response.Sequence <= 0 || response.Current is null)
                        {
                            throw new InvalidDataException("An accepted operation requires a durable receipt.");
                        }

                        journal.Accept(edit.OperationId, response.Sequence, response.Current);
                        accepted++;
                        break;
                    case NoteSubmitStatus.StaleRevision:
                    case NoteSubmitStatus.PathCollision:
                        if (response.Current is not null && response.Current.Id != edit.RecordId)
                        {
                            throw new InvalidDataException("A conflict cannot disclose a foreign record.");
                        }

                        journal.Reject(edit.OperationId,
                            response.Status == NoteSubmitStatus.StaleRevision
                                ? LocalNoteConflictReason.StaleRevision
                                : LocalNoteConflictReason.PathCollision,
                            response.Current);
                        break;
                    case NoteSubmitStatus.Unauthorized:
                        RequireNoPrivateResponse(response);
                        throw new UnauthorizedAccessException("The device is not authorized to synchronize this space.");
                    case NoteSubmitStatus.UnsupportedProtocolVersion:
                        RequireNoPrivateResponse(response);
                        throw new NotSupportedException("The note synchronization protocol version is unsupported.");
                    case NoteSubmitStatus.InvalidRequest:
                    case NoteSubmitStatus.OperationKeyReused:
                        RequireNoPrivateResponse(response);
                        throw new InvalidDataException("The remote endpoint rejected the operation contract.");
                    default:
                        throw new InvalidDataException("The remote endpoint returned an unknown operation result.");
                }
            }

            for (int pageNumber = 0; pageNumber < MaximumPagesPerCycle; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                long cursor = journal.Cursor;
                NotePullResult page = await exchange.PullAsync(
                    new NotePullRequest(ProtocolVersion, securitySpaceId, epoch,
                        cursor, PageLimit), cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (page is null || page.SecuritySpaceId != securitySpaceId ||
                    page.RequestedCursor != cursor || page.Changes is null ||
                    page.Changes.Count > PageLimit)
                {
                    throw new InvalidDataException("The synchronization page does not match the requested scope.");
                }

                if (page.Status == NotePullStatus.WrongCollectionEpoch)
                {
                    RequireEmptyErrorPage(page);
                    return Progress(NoteSyncState.EpochChanged);
                }

                if (page.CollectionEpoch != epoch)
                {
                    throw new InvalidDataException("The synchronization page uses a foreign collection epoch.");
                }

                if (page.Status == NotePullStatus.Unauthorized)
                {
                    RequireEmptyErrorPage(page);
                    throw new UnauthorizedAccessException("The device is not authorized to synchronize this space.");
                }

                if (page.Status == NotePullStatus.UnsupportedProtocolVersion)
                {
                    RequireEmptyErrorPage(page);
                    throw new NotSupportedException("The note synchronization protocol version is unsupported.");
                }

                if (page.Status == NotePullStatus.InvalidCursor)
                {
                    RequireEmptyErrorPage(page);
                    return Progress(NoteSyncState.CursorResetRequired);
                }

                if (page.Status == NotePullStatus.CursorExpired)
                {
                    RequireEmptyErrorPage(page);
                    if (snapshotApplied)
                    {
                        return Progress(NoteSyncState.CursorResetRequired);
                    }

                    NoteSnapshotResult restored = await exchange.SnapshotAsync(
                        securitySpaceId, epoch, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (restored is null || restored.SecuritySpaceId != securitySpaceId ||
                        restored.Snapshot is null || restored.Snapshot.CollectionEpoch != epoch ||
                        restored.Snapshot.Cursor < cursor)
                    {
                        throw new InvalidDataException("The reset snapshot is incompatible with durable local state.");
                    }

                    journal.Rebootstrap(restored.Snapshot);
                    snapshotApplied = true;
                    continue;
                }

                if (page.Status != NotePullStatus.Ok || page.HeadSequence < cursor ||
                    page.NextCursor < cursor || page.NextCursor > page.HeadSequence ||
                    page.NextCursor != (page.Changes.Count == 0
                        ? cursor : page.Changes[^1].Sequence))
                {
                    throw new InvalidDataException("The remote synchronization cursor is inconsistent.");
                }

                journal.ApplyPage(cursor, page.Changes);
                received += page.Changes.Count;
                if (journal.Cursor >= page.HeadSequence)
                {
                    return Progress(journal.Outbound().Count > 0
                        ? NoteSyncState.MoreWork : NoteSyncState.UpToDate);
                }

                if (page.Changes.Count == 0)
                {
                    return Progress(NoteSyncState.MoreWork);
                }
            }

            return Progress(NoteSyncState.MoreWork);

            NoteSyncProgress Progress(NoteSyncState result) => new(result,
                accepted, journal.Conflicts.Count - beforeConflicts, received,
                snapshotApplied, journal.Cursor, journal.Pending.Count,
                journal.Conflicts.Count);
        }
        finally
        {
            cycle.Release();
        }
    }

    private static void RequireNoPrivateResponse(NoteSubmitResult response)
    {
        if (response.Current is not null)
        {
            throw new InvalidDataException("A rejected operation disclosed private content.");
        }
    }

    private static void RequireEmptyErrorPage(NotePullResult response)
    {
        if (response.Changes.Count != 0)
        {
            throw new InvalidDataException("A rejected pull disclosed private content.");
        }
    }
}
