using Xunit;
using static Praxis.Notes.Tests.Synchronization.SyncHarnessInputs;

namespace Praxis.Notes.Tests.Synchronization;

public sealed class SyncRecoveryHarnessTests
{
    [Fact]
    public void LostAcknowledgementAndRestartRetainOperationUntilIdenticalReplay()
    {
        SyncContractFixture server = new(Epoch);
        OfflineSyncReplica device = OfflineSyncReplica.Start(Epoch, FirstDevice);
        SyncMutation operation = NewMutation(FirstDevice, TestOperationId(1), SharedRecord, 0,
            SyncAction.Upsert, "first.md", "local content");
        device.Queue(operation);
        ReplicaCheckpoint durable = device.Checkpoint();

        SyncReply receipt = server.Apply(operation);
        Assert.Equal(ApplyStatus.Applied, receipt.Status);
        device = OfflineSyncReplica.Restart(durable);

        Assert.Single(device.Pending);
        Assert.Equal(receipt, server.Apply(operation));
        device.Acknowledge(operation, receipt);
        device.CommitPage(server.Pull(Epoch, 0, 10));

        Assert.Empty(device.Pending);
        Assert.Empty(device.Conflicts);
        Assert.Equal(1, device.Cursor);
        Assert.Equal(1, server.HeadSequence);
        Assert.Equal(server.Snapshot().Records.ToArray(), device.Checkpoint().Records.ToArray());
    }

    [Fact]
    public void DroppedPullAndRestartBeforeCheckpointReplaysWithoutDataLoss()
    {
        SyncContractFixture server = new(Epoch);
        OfflineSyncReplica client = OfflineSyncReplica.Start(Epoch, SecondDevice);
        Assert.Equal(ApplyStatus.Applied,
            server.Apply(NewMutation(FirstDevice, TestOperationId(1), SharedRecord, 0,
                SyncAction.Upsert, "content.md", "body")).Status);
        SyncPage delivered = server.Pull(Epoch, 0, 10);
        ReplicaCheckpoint durable = client.Checkpoint();

        client = OfflineSyncReplica.Restart(durable);
        Assert.Equal(0, client.Cursor);
        client.CommitPage(delivered);

        OfflineSyncReplica uncheckpointed = client;
        client = OfflineSyncReplica.Restart(durable);
        Assert.Equal(0, client.Cursor);
        Assert.Empty(client.Checkpoint().Records);

        SyncPage retried = server.Pull(Epoch, client.Cursor, 10);
        client.CommitPage(retried);
        client.CommitPage(delivered);

        Assert.Equal(1, uncheckpointed.Cursor);
        Assert.Equal(1, client.Cursor);
        Assert.Equal(server.Snapshot().Records.ToArray(), client.Checkpoint().Records.ToArray());
    }

    [Fact]
    public void OutOfOrderAndMissingPageEventsCannotPartiallyCommitAReplica()
    {
        SyncContractFixture server = new(Epoch);
        OfflineSyncReplica client = OfflineSyncReplica.Start(Epoch, SecondDevice);
        for (int index = 0; index < 3; index++)
        {
            Guid record = Guid.Parse($"00000000-0000-4000-8000-{index + 1:D12}");
            SyncMutation mutation = NewMutation(FirstDevice, TestOperationId(index + 1), record, 0,
                SyncAction.Upsert, $"note-{index}.md", $"{index}");
            Assert.Equal(ApplyStatus.Applied, server.Apply(mutation).Status);
        }

        SyncPage later = server.Pull(Epoch, 2, 1);
        Assert.Throws<InvalidDataException>(() => client.CommitPage(later));
        Assert.Equal(0, client.Cursor);

        SyncPage valid = server.Pull(Epoch, 0, 3);
        SyncPage corrupted = valid with
        {
            Changes =
            [
                valid.Changes[0],
                valid.Changes[2]
            ]
        };
        Assert.Throws<InvalidDataException>(() => client.CommitPage(corrupted));
        Assert.Equal(0, client.Cursor);
        Assert.Empty(client.Checkpoint().Records);

        client.CommitPage(valid);
        client.CommitPage(valid);
        Assert.Equal(3, client.Cursor);
        Assert.Equal(server.Snapshot().Records.ToArray(), client.Checkpoint().Records.ToArray());
    }

    [Fact]
    public void InvalidAcceptedReceiptCannotRemoveAnUnacknowledgedOperation()
    {
        OfflineSyncReplica client = OfflineSyncReplica.Start(Epoch, FirstDevice);
        SyncMutation mutation = NewMutation(FirstDevice, TestOperationId(1), SharedRecord, 0,
            SyncAction.Upsert, "note.md", "pending data");
        client.Queue(mutation);

        SyncReply mismatched = new(ApplyStatus.Applied, 1,
            new SyncRecord(Guid.Empty, 1, "note.md", "wrong identity", false));

        Assert.Throws<InvalidDataException>(() => client.Acknowledge(mutation, mismatched));
        Assert.Equal(mutation, Assert.Single(client.Pending));
        Assert.Empty(client.Conflicts);
    }

    [Fact]
    public void EmptyPageCannotAdvanceTheAppliedCursor()
    {
        OfflineSyncReplica client = OfflineSyncReplica.Start(Epoch, FirstDevice);
        SyncPage forged = new(PullStatus.Ok, 1, 1, []);

        Assert.Throws<InvalidDataException>(() => client.CommitPage(forged));
        Assert.Equal(0, client.Cursor);
        Assert.Empty(client.Checkpoint().Records);
    }

    [Fact]
    public void CompactedHistoryForcesSnapshotButKeepsPendingOfflineEdit()
    {
        SyncContractFixture server = new(Epoch);
        OfflineSyncReplica client = OfflineSyncReplica.Start(Epoch, SecondDevice);
        Assert.Equal(ApplyStatus.Applied,
            server.Apply(NewMutation(FirstDevice, TestOperationId(1), SharedRecord, 0,
                SyncAction.Upsert, "note.md", "original")).Status);
        client.CommitPage(server.Pull(Epoch, 0, 5));

        SyncMutation pending = NewMutation(SecondDevice, TestOperationId(2), SharedRecord, 1,
            SyncAction.Upsert, "note.md", "my offline contribution");
        client.Queue(pending);
        ReplicaCheckpoint checkpoint = client.Checkpoint();
        Assert.Equal(ApplyStatus.Applied,
            server.Apply(NewMutation(FirstDevice, TestOperationId(3), SharedRecord, 1,
                SyncAction.Delete, null, null)).Status);
        server.PruneThrough(2);

        client = OfflineSyncReplica.Restart(checkpoint);
        Assert.Equal(PullStatus.CursorExpired, server.Pull(Epoch, client.Cursor, 10).Status);
        Assert.Throws<InvalidDataException>(() =>
            client.CommitPage(server.Pull(Epoch, client.Cursor, 10)));

        client.Rebootstrap(server.Snapshot());
        Assert.Equal(2, client.Cursor);
        Assert.True(Assert.Single(client.Checkpoint().Records).Deleted);
        Assert.Equal(pending, Assert.Single(client.Pending));

        SyncReply rejected = server.Apply(pending);
        Assert.Equal(ApplyStatus.StaleRevision, rejected.Status);
        client.Acknowledge(pending, rejected);
        Assert.Equal("my offline contribution", Assert.Single(client.Conflicts).Mutation.Body);
        Assert.Empty(client.Pending);
        Assert.Equal(2, server.HeadSequence);
    }

    [Fact]
    public void ChangedCollectionEpochCannotImportAnOldSnapshotOrPullPage()
    {
        SyncContractFixture server = new(Epoch);
        OfflineSyncReplica client = OfflineSyncReplica.Start(Epoch, FirstDevice);
        SyncMutation pending = NewMutation(FirstDevice, TestOperationId(1), SharedRecord, 0,
            SyncAction.Upsert, "offline.md", "not uploaded");
        client.Queue(pending);
        ReplicaCheckpoint durable = client.Checkpoint();
        SyncContractFixture different = new(Guid.Parse("eeeeeeee-1111-4111-8111-111111111111"));

        Assert.Equal(PullStatus.WrongCollectionEpoch,
            different.Pull(Epoch, 0, 10).Status);
        Assert.Throws<InvalidDataException>(() =>
            client.CommitPage(different.Pull(Epoch, 0, 10)));
        Assert.Throws<InvalidDataException>(() =>
            client.Rebootstrap(different.Snapshot()));
        Assert.Equal(0, client.Cursor);
        Assert.Equal(pending, Assert.Single(OfflineSyncReplica.Restart(durable).Pending));
    }

}
