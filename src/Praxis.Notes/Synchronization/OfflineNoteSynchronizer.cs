using Praxis.Notes.Journal;

namespace Praxis.Notes.Synchronization;

/// <summary>
/// Drives a durable offline journal through an authenticated, client-decrypted
/// exchange. A real remote adapter must enforce #15 device and space authorization
/// and client-held encryption before supplying this application-level interface.
/// </summary>
public sealed partial class OfflineNoteSynchronizer(
    OfflineNoteJournal journal, Guid securitySpaceId, INoteSyncExchange exchange)
{
    private const int ProtocolVersion = 1;
    private const int PageLimit = 100;
    private const int MaximumOperationsPerCycle = 128;
    private const int MaximumPagesPerCycle = 32;
    private readonly SemaphoreSlim cycle = new(1, 1);

    public async Task<NoteSyncProgress> ReconcileAsync(
        CancellationToken cancellationToken = default)
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
            Guid epoch = journal.CollectionEpoch;
            Guid deviceId = journal.DeviceId;
            int conflictsBefore = journal.Conflicts.Count;

            (int accepted, NoteSyncState? early) =
                await SubmitPendingAsync(epoch, deviceId, cancellationToken).ConfigureAwait(false);
            if (early is not null)
            {
                return Progress(early.Value, accepted, 0, false, conflictsBefore);
            }

            (NoteSyncState state, int applied, bool snapshot) =
                await PullPagesAsync(epoch, deviceId, cancellationToken).ConfigureAwait(false);
            return Progress(state, accepted, applied, snapshot, conflictsBefore);
        }
        finally
        {
            cycle.Release();
        }
    }

    private NoteSyncProgress Progress(NoteSyncState state, int accepted,
        int applied, bool snapshot, int conflictsBefore) =>
        new(state, accepted, journal.Conflicts.Count - conflictsBefore,
            applied, snapshot, journal.Cursor, journal.Pending.Count,
            journal.Conflicts.Count);
}
