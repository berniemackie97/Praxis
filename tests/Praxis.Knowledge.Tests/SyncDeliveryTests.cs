using Xunit;
using static Praxis.Knowledge.Tests.SyncHarnessInputs;

namespace Praxis.Knowledge.Tests;

public sealed class SyncDeliveryTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(11)]
    [InlineData(101)]
    [InlineData(1009)]
    [InlineData(10103)]
    [InlineData(31337)]
    public void SeededOfflineDeliveryAndDuplicatePullsConverge(int seed)
    {
        Random random = new Random(seed);
        SyncContractFixture server = new SyncContractFixture(Epoch);
        OfflineSyncReplica[] clients = new[] { FirstDevice, SecondDevice, ThirdDevice }
            .Select(device => OfflineSyncReplica.Start(Epoch, device)).ToArray();
        List<(OfflineSyncReplica Replica, SyncMutation Mutation)> deliveries = new List<(OfflineSyncReplica Replica, SyncMutation Mutation)>();

        for (int index = 0; index < clients.Length; index++)
        {
            for (int item = 0; item < 12; item++)
            {
                Guid record = NextGuid(random);
                SyncMutation mutation = NewMutation(
                    clients[index].DeviceId, NextGuid(random), record, 0,
                    SyncAction.Upsert, $"device-{index}/note-{item}.md", $"seed-{seed}-{index}-{item}");
                clients[index].Queue(mutation);
                deliveries.Add((clients[index], mutation));
            }
        }

        Shuffle(deliveries, random);
        foreach ((OfflineSyncReplica replica, SyncMutation mutation) in deliveries)
        {
            SyncReply receipt = server.Apply(mutation);
            Assert.Equal(ApplyStatus.Applied, receipt.Status);
            if (random.Next(4) == 0)
            {
                Assert.Equal(receipt, server.Apply(mutation));
            }

            if (random.Next(3) != 0)
            {
                replica.Acknowledge(mutation, receipt);
            }
        }

        foreach (OfflineSyncReplica replica in clients)
        {
            foreach (SyncMutation pending in replica.Pending.ToArray())
            {
                long before = server.HeadSequence;
                SyncReply receipt = server.Apply(pending);
                Assert.Equal(ApplyStatus.Applied, receipt.Status);
                Assert.True(receipt.HeadSequence <= before);
                Assert.Equal(before, server.HeadSequence);
                replica.Acknowledge(pending, receipt);
            }

            Assert.Empty(replica.Pending);
            Assert.Empty(replica.Conflicts);
        }

        SyncRecord[] expected = server.Snapshot().Records.ToArray();
        Assert.Equal((long)deliveries.Count, server.HeadSequence);
        for (int index = 0; index < clients.Length; index++)
        {
            OfflineSyncReplica replica = clients[index];
            while (replica.Cursor < server.HeadSequence)
            {
                SyncPage page = server.Pull(Epoch, replica.Cursor, random.Next(1, 8));
                Assert.Equal(PullStatus.Ok, page.Status);
                Assert.NotEmpty(page.Changes);
                ReplicaCheckpoint checkpoint = replica.Checkpoint();

                if (random.Next(3) == 0)
                {
                    replica = OfflineSyncReplica.Restart(checkpoint);
                    Assert.Equal(checkpoint.Cursor, replica.Cursor);
                }

                replica.CommitPage(page);
                if (random.Next(4) == 0)
                {
                    replica.CommitPage(page);
                }
            }

            clients[index] = OfflineSyncReplica.Restart(replica.Checkpoint());
            Assert.True(expected.SequenceEqual(clients[index].Checkpoint().Records),
                $"Seed {seed}, client {index}: replica diverged after replay.");
            Assert.Equal(server.HeadSequence, clients[index].Cursor);
        }
    }

}
