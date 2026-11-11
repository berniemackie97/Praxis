using Praxis.Notes.Journal;

namespace Praxis.Notes.Synchronization;

public sealed partial class OfflineNoteSynchronizer
{
    private async Task<(NoteSyncState State, int Applied, bool Snapshot)> PullPagesAsync(
        Guid epoch, Guid deviceId, CancellationToken cancellationToken)
    {
        int applied = 0;
        bool snapshotApplied = false;
        for (int pageNumber = 0; pageNumber < MaximumPagesPerCycle; pageNumber++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long cursor = journal.Cursor;
            NotePullResult page = await exchange.PullAsync(
                new NotePullRequest(ProtocolVersion, securitySpaceId, epoch,
                    deviceId, cursor, PageLimit), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (page is null || page.ProtocolVersion != ProtocolVersion ||
                page.SecuritySpaceId != securitySpaceId || page.DeviceId != deviceId ||
                page.RequestedCursor != cursor || page.Changes is null ||
                page.Changes.Count > PageLimit)
            {
                throw new InvalidDataException("The synchronization page does not match its request.");
            }

            if (page.Status == NotePullStatus.WrongCollectionEpoch)
            {
                RequireEmptyErrorPage(page);
                return (NoteSyncState.EpochChanged, applied, snapshotApplied);
            }

            if (page.CollectionEpoch != epoch)
            {
                throw new InvalidDataException("The page uses a foreign collection epoch.");
            }

            switch (page.Status)
            {
                case NotePullStatus.Unauthorized:
                    RequireEmptyErrorPage(page);
                    throw new UnauthorizedAccessException(
                        "The device is not authorized to synchronize this space.");
                case NotePullStatus.UnsupportedProtocolVersion:
                    RequireEmptyErrorPage(page);
                    throw new NotSupportedException("The note protocol version is unsupported.");
                case NotePullStatus.InvalidCursor:
                    RequireEmptyErrorPage(page);
                    return (NoteSyncState.CursorResetRequired, applied, snapshotApplied);
                case NotePullStatus.CursorExpired:
                    RequireEmptyErrorPage(page);
                    if (snapshotApplied)
                    {
                        return (NoteSyncState.CursorResetRequired, applied, snapshotApplied);
                    }

                    await ApplySnapshotAsync(epoch, deviceId, cursor, cancellationToken)
                        .ConfigureAwait(false);
                    snapshotApplied = true;
                    continue;
                case NotePullStatus.Ok:
                    break;
                default:
                    throw new InvalidDataException("The endpoint returned an unknown page result.");
            }

            if (page.HeadSequence < cursor || page.NextCursor < cursor ||
                page.NextCursor > page.HeadSequence ||
                page.NextCursor != (page.Changes.Count == 0
                    ? cursor : page.Changes[^1].Sequence))
            {
                throw new InvalidDataException("The remote synchronization cursor is inconsistent.");
            }

            journal.ApplyPage(cursor, page.Changes);
            applied += page.Changes.Count;
            if (journal.Cursor >= page.HeadSequence)
            {
                return (journal.Outbound().Count > 0
                    ? NoteSyncState.MoreWork : NoteSyncState.UpToDate, applied, snapshotApplied);
            }

            if (page.Changes.Count == 0)
            {
                return (NoteSyncState.MoreWork, applied, snapshotApplied);
            }
        }

        return (NoteSyncState.MoreWork, applied, snapshotApplied);
    }

    private async Task ApplySnapshotAsync(Guid epoch, Guid deviceId, long cursor,
        CancellationToken cancellationToken)
    {
        NoteSnapshotResult restored = await exchange.SnapshotAsync(
            securitySpaceId, epoch, deviceId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (restored is null || restored.ProtocolVersion != ProtocolVersion ||
            restored.SecuritySpaceId != securitySpaceId || restored.DeviceId != deviceId ||
            restored.Snapshot is null || restored.Snapshot.CollectionEpoch != epoch ||
            restored.Snapshot.Cursor < cursor)
        {
            throw new InvalidDataException("The reset snapshot is incompatible with the local journal.");
        }

        ValidateSnapshot(restored.Snapshot, journal);
        journal.Rebootstrap(restored.Snapshot);
    }

    private static void ValidateSnapshot(LocalNoteSnapshot snapshot, OfflineNoteJournal local)
    {
        if (snapshot.Records is null)
        {
            throw new InvalidDataException("The reset snapshot has no record projection.");
        }

        Dictionary<Guid, LocalNoteState> incoming = new();
        foreach (LocalNoteState record in snapshot.Records)
        {
            if (record is null || !incoming.TryAdd(record.Id, record))
            {
                throw new InvalidDataException("The reset snapshot contains duplicate or invalid records.");
            }
        }

        // An accepted mutation and a previously durable projection must not vanish.
        foreach (AcceptedNoteReceipt receipt in local.AcknowledgedChanges)
        {
            if (receipt.Sequence > snapshot.Cursor ||
                !incoming.TryGetValue(receipt.Record.Id, out LocalNoteState? current) ||
                current.Revision < receipt.Record.Revision ||
                current.Revision == receipt.Record.Revision && current != receipt.Record)
            {
                throw new InvalidDataException("The reset contradicts a durable accepted receipt.");
            }
        }

        foreach (LocalNoteState existing in local.Records)
        {
            if (!incoming.TryGetValue(existing.Id, out LocalNoteState? remote) ||
                remote.Revision < existing.Revision)
            {
                throw new InvalidDataException("The reset rolls back a durable local record.");
            }
        }
    }

    private static void RequireEmptyErrorPage(NotePullResult page)
    {
        if (page.Changes.Count != 0 || page.HeadSequence != 0 ||
            page.NextCursor != page.RequestedCursor)
        {
            throw new InvalidDataException("A rejected pull disclosed private state.");
        }
    }
}
