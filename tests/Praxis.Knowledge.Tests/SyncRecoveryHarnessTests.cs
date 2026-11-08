using Xunit;
using static Praxis.Knowledge.Tests.SyncHarnessInputs;

namespace Praxis.Knowledge.Tests;

public sealed class SyncRecoveryHarnessTests
{
    [Fact]
    public void LostAcknowledgementAndRestartRetainOperationUntilIdenticalReplay()
    {
        var server = new SyncContractFixture(Epoch);
        var device = OfflineSyncReplica.Start(Epoch, FirstDevice);
        var operation = NewMutation(FirstDevice, Guid.NewGuid(), SharedRecord, 0,
            SyncAction.Upsert, "first.md", "local content");
        device.Queue(operation);
        var durable = device.Checkpoint();

        var receipt = server.Apply(operation);
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
        var server = new SyncContractFixture(Epoch);
        var client = OfflineSyncReplica.Start(Epoch, SecondDevice);
        Assert.Equal(ApplyStatus.Applied,
            server.Apply(NewMutation(FirstDevice, Guid.NewGuid(), SharedRecord, 0,
                SyncAction.Upsert, "content.md", "body")).Status);
        var delivered = server.Pull(Epoch, 0, 10);
        var durable = client.Checkpoint();

        client = OfflineSyncReplica.Restart(durable);
        Assert.Equal(0, client.Cursor);
        client.CommitPage(delivered);

        var uncheckpointed = client;
        client = OfflineSyncReplica.Restart(durable);
        Assert.Equal(0, client.Cursor);
        Assert.Empty(client.Checkpoint().Records);

        var retried = server.Pull(Epoch, client.Cursor, 10);
        client.CommitPage(retried);
        client.CommitPage(delivered);

        Assert.Equal(1, uncheckpointed.Cursor);
        Assert.Equal(1, client.Cursor);
        Assert.Equal(server.Snapshot().Records.ToArray(), client.Checkpoint().Records.ToArray());
    }

    [Fact]
    public void OutOfOrderAndMissingPageEventsCannotPartiallyCommitAReplica()
    {
        var server = new SyncContractFixture(Epoch);
        var client = OfflineSyncReplica.Start(Epoch, SecondDevice);
        for (var index = 0; index < 3; index++)
        {
            var record = Guid.Parse($"00000000-0000-4000-8000-{index + 1:D12}");
            var mutation = NewMutation(FirstDevice, Guid.NewGuid(), record, 0,
                SyncAction.Upsert, $"note-{index}.md", $"{index}");
            Assert.Equal(ApplyStatus.Applied, server.Apply(mutation).Status);
        }

        var later = server.Pull(Epoch, 2, 1);
        Assert.Throws<InvalidDataException>(() => client.CommitPage(later));
        Assert.Equal(0, client.Cursor);

        var valid = server.Pull(Epoch, 0, 3);
        var corrupted = valid with
        {
            Changes = new[]
            {
                valid.Changes[0],
                valid.Changes[2]
            }
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
        var client = OfflineSyncReplica.Start(Epoch, FirstDevice);
        var mutation = NewMutation(FirstDevice, Guid.NewGuid(), SharedRecord, 0,
            SyncAction.Upsert, "note.md", "pending data");
        client.Queue(mutation);

        var mismatched = new SyncReply(ApplyStatus.Applied, 1,
            new SyncRecord(Guid.NewGuid(), 1, "note.md", "wrong identity", false));

        Assert.Throws<InvalidDataException>(() => client.Acknowledge(mutation, mismatched));
        Assert.Equal(mutation, Assert.Single(client.Pending));
        Assert.Empty(client.Conflicts);
    }

    [Fact]
    public void EmptyPageCannotAdvanceTheAppliedCursor()
    {
        var client = OfflineSyncReplica.Start(Epoch, FirstDevice);
        var forged = new SyncPage(PullStatus.Ok, 1, 1, []);

        Assert.Throws<InvalidDataException>(() => client.CommitPage(forged));
        Assert.Equal(0, client.Cursor);
        Assert.Empty(client.Checkpoint().Records);
    }

    [Fact]
    public void CompactedHistoryForcesSnapshotButKeepsPendingOfflineEdit()
    {
        var server = new SyncContractFixture(Epoch);
        var client = OfflineSyncReplica.Start(Epoch, SecondDevice);
        Assert.Equal(ApplyStatus.Applied,
            server.Apply(NewMutation(FirstDevice, Guid.NewGuid(), SharedRecord, 0,
                SyncAction.Upsert, "note.md", "original")).Status);
        client.CommitPage(server.Pull(Epoch, 0, 5));

        var pending = NewMutation(SecondDevice, Guid.NewGuid(), SharedRecord, 1,
            SyncAction.Upsert, "note.md", "my offline contribution");
        client.Queue(pending);
        var checkpoint = client.Checkpoint();
        Assert.Equal(ApplyStatus.Applied,
            server.Apply(NewMutation(FirstDevice, Guid.NewGuid(), SharedRecord, 1,
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

        var rejected = server.Apply(pending);
        Assert.Equal(ApplyStatus.StaleRevision, rejected.Status);
        client.Acknowledge(pending, rejected);
        Assert.Equal("my offline contribution", Assert.Single(client.Conflicts).Mutation.Body);
        Assert.Empty(client.Pending);
        Assert.Equal(2, server.HeadSequence);
    }

    [Fact]
    public void ChangedCollectionEpochCannotImportAnOldSnapshotOrPullPage()
    {
        var server = new SyncContractFixture(Epoch);
        var client = OfflineSyncReplica.Start(Epoch, FirstDevice);
        var pending = NewMutation(FirstDevice, Guid.NewGuid(), SharedRecord, 0,
            SyncAction.Upsert, "offline.md", "not uploaded");
        client.Queue(pending);
        var durable = client.Checkpoint();
        var different = new SyncContractFixture(Guid.Parse("eeeeeeee-1111-4111-8111-111111111111"));

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
