using Praxis.Notes.Journal;
using Praxis.Notes.Synchronization;
using Xunit;

namespace Praxis.Notes.Tests.Synchronization;

public sealed class OfflineNoteSynchronizerTests : IDisposable
{
    private static readonly Guid Space = Guid.Parse("6a622da4-8cab-4c31-9b89-c4f1bba1b0ab");
    private static readonly Guid Epoch = Guid.Parse("279928f3-8ed5-4ec2-9f1b-077bc4886ce2");
    private static readonly Guid DeviceA = Guid.Parse("e11668a9-e073-4bcf-8882-7fbca8c2023d");
    private static readonly Guid DeviceB = Guid.Parse("d67b26c6-5ee4-485f-88aa-382956baea0f");
    private readonly string directory = Path.Combine(Path.GetTempPath(),
        "praxis-reconcile-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TwoPersistedClientsRetainOfflineConflictAndConvergeAfterExplicitResolution()
    {
        JournalSyncExchangeFixture server = new(Space, Epoch);
        Guid record = Guid.NewGuid();

        using (OfflineNoteJournal first = Open(DeviceA))
        using (OfflineNoteJournal second = Open(DeviceB))
        {
            OfflineNoteSynchronizer firstSync = new(first, Space, server);
            OfflineNoteSynchronizer secondSync = new(second, Space, server);
            first.Queue(record, LocalNoteAction.Upsert, "shared/note.md", "base");
            Assert.Equal(NoteSyncState.UpToDate, (await firstSync.ReconcileAsync()).State);
            await secondSync.ReconcileAsync();
            Assert.Equal(first.Records.ToArray(), second.Records.ToArray());

            PendingNoteEdit firstOffline = first.Queue(record, LocalNoteAction.Upsert,
                "shared/note.md", "first offline edit");
            second.Queue(record, LocalNoteAction.Upsert,
                "shared/note.md", "second offline edit");
            await secondSync.ReconcileAsync();
            NoteSyncProgress conflict = await firstSync.ReconcileAsync();
            Assert.Equal(NoteSyncState.UpToDate, conflict.State);
            Assert.Equal(1, conflict.NewConflicts);
            RejectedNoteEdit rejected = Assert.Single(first.Conflicts);
            Assert.Equal(firstOffline.OperationId, rejected.Edit.OperationId);
            Assert.Equal("first offline edit", Assert.Single(first.Pending).Markdown);
            Assert.Equal("second offline edit", Assert.Single(first.Records).Markdown);

            first.Resolve(rejected.Edit.OperationId, LocalNoteAction.Upsert,
                "shared/note.md", "combined after review");
            await firstSync.ReconcileAsync();
            await secondSync.ReconcileAsync();
            Assert.Empty(first.Pending);
            Assert.Empty(first.Conflicts);
            Assert.Single(first.ResolvedConflicts);
            Assert.Empty(second.Pending);
            Assert.Equal(first.Records.ToArray(), second.Records.ToArray());
            Assert.Equal("combined after review", Assert.Single(second.Records).Markdown);
        }

        using OfflineNoteJournal reopenedFirst = Open(DeviceA);
        using OfflineNoteJournal reopenedSecond = Open(DeviceB);
        Assert.Equal(3, reopenedFirst.Cursor);
        Assert.Equal(3, reopenedSecond.Cursor);
        Assert.Equal(reopenedFirst.Records.ToArray(), reopenedSecond.Records.ToArray());
        Assert.Equal("first offline edit",
            Assert.Single(reopenedFirst.ResolvedConflicts).Edit.Markdown);
    }

    [Fact]
    public async Task CommittedButLostSubmitAcknowledgmentRetriesSameOperationAfterRestart()
    {
        JournalSyncExchangeFixture server = new(Space, Epoch)
        {
            DropNextSubmitAcknowledgment = true
        };
        Guid record = Guid.NewGuid();
        Guid operation;

        using (OfflineNoteJournal first = Open(DeviceA))
        {
            operation = first.Queue(record, LocalNoteAction.Upsert, "lost-ack.md", "local original").OperationId;
            OfflineNoteSynchronizer synchronizer = new(first, Space, server);
            await Assert.ThrowsAsync<IOException>(() => synchronizer.ReconcileAsync());
            Assert.Equal(1, server.Server.HeadSequence);
            Assert.Equal(operation, Assert.Single(first.Pending).OperationId);
            Assert.Equal(0, first.Cursor);
        }

        using (OfflineNoteJournal restarted = Open(DeviceA))
        {
            OfflineNoteSynchronizer synchronizer = new(restarted, Space, server);
            NoteSyncProgress progress = await synchronizer.ReconcileAsync();
            Assert.Equal(1, progress.AcceptedOperations);
            Assert.Equal(1, progress.AppliedChanges);
            Assert.Equal(1, restarted.Cursor);
            Assert.Empty(restarted.Pending);
            Assert.Empty(restarted.AcknowledgedChanges);
            Assert.Equal("local original", Assert.Single(restarted.Records).Markdown);
            Assert.Equal(1, server.Server.HeadSequence);
            Assert.Equal(2, server.TotalSubmits);
        }
    }

    [Fact]
    public async Task ConcurrentDeleteAndOfflineEditRequireExplicitRestoreWithoutResurrection()
    {
        JournalSyncExchangeFixture server = new(Space, Epoch);
        Guid record = Guid.NewGuid();
        using OfflineNoteJournal first = Open(DeviceA);
        using OfflineNoteJournal second = Open(DeviceB);
        OfflineNoteSynchronizer firstSync = new(first, Space, server);
        OfflineNoteSynchronizer secondSync = new(second, Space, server);

        first.Queue(record, LocalNoteAction.Upsert, "history.md", "original");
        await firstSync.ReconcileAsync();
        await secondSync.ReconcileAsync();
        PendingNoteEdit losingEdit = first.Queue(record, LocalNoteAction.Upsert,
            "history.md", "offline changed");
        second.Queue(record, LocalNoteAction.Delete);
        await secondSync.ReconcileAsync();
        await firstSync.ReconcileAsync();

        Assert.Equal(losingEdit.OperationId, Assert.Single(first.Conflicts).Edit.OperationId);
        Assert.Equal("offline changed", Assert.Single(first.Pending).Markdown);
        LocalNoteState tombstone = Assert.Single(first.Records);
        Assert.True(tombstone.Deleted);
        Assert.Equal("original", tombstone.Markdown);
        Assert.Equal(2, tombstone.Revision);

        first.Resolve(losingEdit.OperationId, LocalNoteAction.Restore,
            "history.md", "original + reviewed offline change");
        await firstSync.ReconcileAsync();
        await secondSync.ReconcileAsync();
        Assert.Equal(first.Records.ToArray(), second.Records.ToArray());
        Assert.False(Assert.Single(second.Records).Deleted);
        Assert.Equal(3, Assert.Single(second.Records).Revision);
        Assert.Equal(3, server.Server.HeadSequence);
    }

    [Fact]
    public async Task RenameCollisionKeepsLosingPathUntilExplicitResolution()
    {
        JournalSyncExchangeFixture server = new(Space, Epoch);
        using OfflineNoteJournal first = Open(DeviceA);
        using OfflineNoteJournal second = Open(DeviceB);
        OfflineNoteSynchronizer firstSync = new(first, Space, server);
        OfflineNoteSynchronizer secondSync = new(second, Space, server);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        first.Queue(a, LocalNoteAction.Upsert, "one.md", "one");
        await firstSync.ReconcileAsync();
        await secondSync.ReconcileAsync();

        PendingNoteEdit rename = second.Queue(a, LocalNoteAction.Upsert, "TWO.md", "renamed one");
        first.Queue(b, LocalNoteAction.Upsert, "two.md", "two");
        await firstSync.ReconcileAsync();
        await secondSync.ReconcileAsync();
        Assert.Equal(LocalNoteConflictReason.PathCollision,
            Assert.Single(second.Conflicts).Reason);
        Assert.Equal(rename.OperationId, Assert.Single(second.Pending).OperationId);
        Assert.Equal("TWO.md", Assert.Single(second.Pending).Path);
        Assert.DoesNotContain(second.Records, note => note.Id == b);
        IncomingNoteCollision retained = Assert.Single(second.IncomingCollisions);
        Assert.Equal(b, retained.Remote.Id);
        Assert.Equal("two.md", retained.Remote.Path);

        second.Resolve(rename.OperationId, LocalNoteAction.Upsert,
            "three.md", "renamed one");
        Assert.Empty(second.IncomingCollisions);
        Assert.Contains(second.Records, note => note.Id == b && note.Path == "two.md");
        await secondSync.ReconcileAsync();
        await firstSync.ReconcileAsync();
        Assert.Equal(first.Records.ToArray(), second.Records.ToArray());
        Assert.Contains(second.Records, r => r.Id == a && r.Path == "three.md");
    }

    [Fact]
    public async Task ExpiredCursorRebootstrapsSameEpochWithoutDroppingConflictsOrQueuedEdits()
    {
        JournalSyncExchangeFixture server = new(Space, Epoch);
        using OfflineNoteJournal first = Open(DeviceA);
        using OfflineNoteJournal second = Open(DeviceB);
        OfflineNoteSynchronizer firstSync = new(first, Space, server);
        OfflineNoteSynchronizer secondSync = new(second, Space, server);
        Guid note = Guid.NewGuid();

        first.Queue(note, LocalNoteAction.Upsert, "note.md", "base");
        await firstSync.ReconcileAsync();
        await secondSync.ReconcileAsync();
        PendingNoteEdit pending = second.Queue(note, LocalNoteAction.Upsert,
            "note.md", "unsent contribution");
        second.Reject(pending.OperationId, LocalNoteConflictReason.StaleRevision,
            Assert.Single(second.Records));
        for (int i = 0; i < 3; i++)
        {
            first.Queue(note, LocalNoteAction.Upsert, "note.md", "updated-" + i);
            await firstSync.ReconcileAsync();
        }

        server.Server.PruneThrough(3);
        NoteSyncProgress progress = await secondSync.ReconcileAsync();
        Assert.True(progress.SnapshotApplied);
        Assert.Equal(4, second.Cursor);
        Assert.Equal(pending.OperationId, Assert.Single(second.Pending).OperationId);
        Assert.Equal("unsent contribution", Assert.Single(second.Pending).Markdown);
        Assert.NotEmpty(second.Conflicts);
        Assert.Equal("updated-2", Assert.Single(second.Records).Markdown);
    }

    [Fact]
    public async Task WrongEpochAndInvalidCursorRequireExplicitRecoveryWithOutboxUnchanged()
    {
        JournalSyncExchangeFixture server = new(Space, Epoch);
        using OfflineNoteJournal first = Open(DeviceA);
        OfflineNoteSynchronizer synchronizer = new(first, Space, server);
        Guid note = Guid.NewGuid();
        PendingNoteEdit pending = first.Queue(note, LocalNoteAction.Upsert, "unsent.md", "retain");

        server.AlterNextSubmit = result => result with
        {
            Status = NoteSubmitStatus.WrongCollectionEpoch,
            Sequence = 0,
            Current = null
        };
        Assert.Equal(NoteSyncState.EpochChanged, (await synchronizer.ReconcileAsync()).State);
        Assert.Equal(pending.OperationId, Assert.Single(first.Pending).OperationId);
        Assert.Equal(0, first.Cursor);

        first.Reject(pending.OperationId, LocalNoteConflictReason.StaleRevision, null);
        server.AlterNextPull = result => result with
        {
            Status = NotePullStatus.InvalidCursor,
            Changes = [],
            HeadSequence = 0,
            NextCursor = result.RequestedCursor
        };
        Assert.Equal(NoteSyncState.CursorResetRequired,
            (await synchronizer.ReconcileAsync()).State);
        Assert.Single(first.Pending);
        Assert.Single(first.Conflicts);
        Assert.Equal(0, first.Cursor);
    }

    [Fact]
    public async Task ContentKeyResetDoesNotDiscardThePendingEditOrAdvanceItsCursor()
    {
        JournalSyncExchangeFixture server = new(Space, Epoch);
        using OfflineNoteJournal journal = Open(DeviceA);
        OfflineNoteSynchronizer synchronizer = new(journal, Space, server);
        PendingNoteEdit edit = journal.Queue(Guid.NewGuid(), LocalNoteAction.Upsert,
            "offline.md", "private unsent content");

        server.AlterNextSubmit = reply => reply with
        {
            Status = NoteSubmitStatus.ResetRequired,
            Sequence = 0,
            Current = null
        };
        Assert.Equal(NoteSyncState.KeyResetRequired,
            (await synchronizer.ReconcileAsync()).State);
        Assert.Equal(edit, Assert.Single(journal.Pending));
        Assert.Equal(0, journal.Cursor);

        journal.Reject(edit.OperationId, LocalNoteConflictReason.StaleRevision, null);
        server.AlterNextPull = page => page with
        {
            Status = NotePullStatus.ResetRequired,
            HeadSequence = 0,
            Changes = [],
            NextCursor = page.RequestedCursor
        };
        Assert.Equal(NoteSyncState.KeyResetRequired,
            (await synchronizer.ReconcileAsync()).State);
        Assert.Equal(edit, Assert.Single(journal.Pending));
        Assert.Equal(0, journal.Cursor);
        Assert.Empty(journal.Records);
    }

    [Fact]
    public async Task LostPullAndMalformedPagesCannotAdvanceDurableCursor()
    {
        JournalSyncExchangeFixture server = new(Space, Epoch);
        using OfflineNoteJournal first = Open(DeviceA);
        using OfflineNoteJournal second = Open(DeviceB);
        OfflineNoteSynchronizer firstSync = new(first, Space, server);
        OfflineNoteSynchronizer secondSync = new(second, Space, server);
        first.Queue(Guid.NewGuid(), LocalNoteAction.Upsert, "one.md", "one");
        await firstSync.ReconcileAsync();

        server.DropNextPullResponse = true;
        await Assert.ThrowsAsync<IOException>(() => secondSync.ReconcileAsync());
        Assert.Equal(0, second.Cursor);

        server.AlterNextPull = page => page with
        {
            Changes = [new AppliedNoteChange(4, new LocalNoteState(
                Guid.NewGuid(), 1, "fake.md", "injected", false))],
            NextCursor = 4,
            HeadSequence = 4
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => secondSync.ReconcileAsync());
        Assert.Equal(0, second.Cursor);
        Assert.Empty(second.Records);

        await secondSync.ReconcileAsync();
        Assert.Equal(1, second.Cursor);
        Assert.Equal("one", Assert.Single(second.Records).Markdown);
    }

    [Fact]
    public async Task RevokedClientCannotPushOrPullAndCannotReceivePrivateErrorBody()
    {
        JournalSyncExchangeFixture server = new(Space, Epoch);
        using OfflineNoteJournal first = Open(DeviceA);
        OfflineNoteSynchronizer synchronizer = new(first, Space, server);
        PendingNoteEdit pending = first.Queue(Guid.NewGuid(), LocalNoteAction.Upsert,
            "private.md", "do not disclose");
        server.RevokedDevices.Add(DeviceA);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => synchronizer.ReconcileAsync());
        Assert.Equal(0, first.Cursor);
        Assert.Equal(pending.OperationId, Assert.Single(first.Pending).OperationId);

        server.RevokedDevices.Remove(DeviceA);
        server.AlterNextSubmit = result => result with
        {
            Status = NoteSubmitStatus.Unauthorized,
            Current = new LocalNoteState(Guid.NewGuid(), 1, "foreign.md", "secret", false)
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => synchronizer.ReconcileAsync());
        Assert.Single(first.Pending);
        Assert.Equal(0, first.Cursor);
    }

    [Fact]
    public async Task CancelledReconciliationPreservesJournalForLaterRetry()
    {
        JournalSyncExchangeFixture server = new(Space, Epoch);
        using OfflineNoteJournal journal = Open(DeviceA);
        OfflineNoteSynchronizer synchronizer = new(journal, Space, server);
        journal.Queue(Guid.NewGuid(), LocalNoteAction.Upsert, "cancel.md", "queued");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            synchronizer.ReconcileAsync(cancellation.Token));
        Assert.Single(journal.Pending);
        Assert.Equal(0, server.Server.HeadSequence);
        Assert.Equal(1, (await synchronizer.ReconcileAsync()).AcceptedOperations);
    }

    [Fact]
    public async Task WorkBudgetStopsAndResumesWithoutDroppingOfflineOutbox()
    {
        JournalSyncExchangeFixture server = new(Space, Epoch);
        using OfflineNoteJournal journal = Open(DeviceA);
        OfflineNoteSynchronizer synchronizer = new(journal, Space, server);
        for (int index = 0; index < 132; index++)
        {
            journal.Queue(Guid.NewGuid(), LocalNoteAction.Upsert,
                $"batch/{index:000}.md", "value-" + index);
        }

        NoteSyncProgress first = await synchronizer.ReconcileAsync();
        Assert.Equal(NoteSyncState.MoreWork, first.State);
        Assert.Equal(128, first.AcceptedOperations);
        Assert.Equal(4, first.PendingOperations);
        Assert.Equal(128, journal.Cursor);
        NoteSyncProgress remaining = await synchronizer.ReconcileAsync();
        Assert.Equal(NoteSyncState.UpToDate, remaining.State);
        Assert.Equal(4, remaining.AcceptedOperations);
        Assert.Empty(journal.Pending);
        Assert.Equal(132, journal.Cursor);
    }

    [Fact]
    public async Task MismatchedReceiptKeepsOriginalOperationForIdempotentRetry()
    {
        JournalSyncExchangeFixture server = new(Space, Epoch);
        using OfflineNoteJournal journal = Open(DeviceA);
        OfflineNoteSynchronizer sync = new(journal, Space, server);
        PendingNoteEdit pending = journal.Queue(Guid.NewGuid(), LocalNoteAction.Upsert,
            "journal.md", "original contribution");
        server.AlterNextSubmit = result => result with { OperationId = Guid.NewGuid() };
        await Assert.ThrowsAsync<InvalidDataException>(() => sync.ReconcileAsync());
        Assert.Equal(1, server.Server.HeadSequence);
        Assert.Equal(pending.OperationId, Assert.Single(journal.Pending).OperationId);
        Assert.Equal(0, journal.Cursor);

        await sync.ReconcileAsync();
        Assert.Equal(1, server.Server.HeadSequence);
        Assert.Equal(1, journal.Cursor);
        Assert.Empty(journal.Pending);
        Assert.Empty(journal.AcknowledgedChanges);
    }

    [Fact]
    public async Task SnapshotCannotRollBackAnAlreadyDurablyAppliedRecord()
    {
        JournalSyncExchangeFixture server = new(Space, Epoch);
        using OfflineNoteJournal first = Open(DeviceA);
        using OfflineNoteJournal second = Open(DeviceB);
        OfflineNoteSynchronizer firstSync = new(first, Space, server);
        OfflineNoteSynchronizer secondSync = new(second, Space, server);
        Guid note = Guid.NewGuid();

        first.Queue(note, LocalNoteAction.Upsert, "record.md", "revision one");
        await firstSync.ReconcileAsync();
        await secondSync.ReconcileAsync();
        first.Queue(note, LocalNoteAction.Upsert, "record.md", "revision two");
        await firstSync.ReconcileAsync();
        first.Queue(note, LocalNoteAction.Upsert, "record.md", "revision three");
        await firstSync.ReconcileAsync();

        server.Server.PruneThrough(2);
        server.AlterNextSnapshot = reply => reply with
        {
            Snapshot = new LocalNoteSnapshot(Epoch, 3,
                [new LocalNoteState(note, 0, "record.md", "rolled back", false)])
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => secondSync.ReconcileAsync());
        Assert.Equal(1, second.Cursor);
        Assert.Equal("revision one", Assert.Single(second.Records).Markdown);
        await secondSync.ReconcileAsync();
        Assert.Equal(3, second.Cursor);
        Assert.Equal("revision three", Assert.Single(second.Records).Markdown);
    }

    [Fact]
    public async Task UnauthorizedPullWithPrivateMetadataIsRejectedWithoutCheckpointMutation()
    {
        JournalSyncExchangeFixture server = new(Space, Epoch);
        using OfflineNoteJournal journal = Open(DeviceA);
        OfflineNoteSynchronizer sync = new(journal, Space, server);
        server.AlterNextPull = response => response with
        {
            Status = NotePullStatus.Unauthorized,
            HeadSequence = 42,
            Changes = []
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => sync.ReconcileAsync());
        Assert.Equal(0, journal.Cursor);
        Assert.Empty(journal.Records);
        Assert.Equal(NoteSyncState.UpToDate, (await sync.ReconcileAsync()).State);
    }

    [Fact]
    public async Task ResponseProtocolOrDeviceSubstitutionCannotAdvanceLocalCheckpoint()
    {
        JournalSyncExchangeFixture server = new(Space, Epoch);
        using OfflineNoteJournal first = Open(DeviceA);
        OfflineNoteSynchronizer synchronizer = new(first, Space, server);
        first.Queue(Guid.NewGuid(), LocalNoteAction.Upsert, "version.md", "local");
        server.AlterNextSubmit = receipt => receipt with { ProtocolVersion = 2 };
        await Assert.ThrowsAsync<InvalidDataException>(() => synchronizer.ReconcileAsync());
        Assert.Equal(0, first.Cursor);
        Assert.Single(first.Pending);

        await synchronizer.ReconcileAsync();
        Assert.Equal(1, first.Cursor);
        server.AlterNextPull = page => page with { DeviceId = DeviceB };
        await Assert.ThrowsAsync<InvalidDataException>(() => synchronizer.ReconcileAsync());
        Assert.Equal(1, first.Cursor);
    }

    private OfflineNoteJournal Open(Guid deviceId) =>
        OfflineNoteJournal.Open(Path.Combine(directory, deviceId.ToString("N")), Epoch, deviceId);

    public void Dispose()
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
