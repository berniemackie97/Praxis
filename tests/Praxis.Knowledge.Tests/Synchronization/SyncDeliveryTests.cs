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
        var random = new Random(seed);
        var server = new SyncContractFixture(Epoch);
        var clients = new[] { FirstDevice, SecondDevice, ThirdDevice }
            .Select(device => OfflineSyncReplica.Start(Epoch, device)).ToArray();
        var deliveries = new List<(OfflineSyncReplica Replica, SyncMutation Mutation)>();

        for (var index = 0; index < clients.Length; index++)
        {
            for (var item = 0; item < 12; item++)
            {
                var record = NextGuid(random);
                var mutation = NewMutation(
                    clients[index].DeviceId, NextGuid(random), record, 0,
                    SyncAction.Upsert, $"device-{index}/note-{item}.md", $"seed-{seed}-{index}-{item}");
                clients[index].Queue(mutation);
                deliveries.Add((clients[index], mutation));
            }
        }

        Shuffle(deliveries, random);
        foreach (var (replica, mutation) in deliveries)
        {
            var receipt = server.Apply(mutation);
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

        foreach (var replica in clients)
        {
            foreach (var pending in replica.Pending.ToArray())
            {
                var before = server.HeadSequence;
                var receipt = server.Apply(pending);
                Assert.Equal(ApplyStatus.Applied, receipt.Status);
                Assert.True(receipt.HeadSequence <= before);
                Assert.Equal(before, server.HeadSequence);
                replica.Acknowledge(pending, receipt);
            }

            Assert.Empty(replica.Pending);
            Assert.Empty(replica.Conflicts);
        }

        var expected = server.Snapshot().Records.ToArray();
        Assert.Equal((long)deliveries.Count, server.HeadSequence);
        for (var index = 0; index < clients.Length; index++)
        {
            var replica = clients[index];
            while (replica.Cursor < server.HeadSequence)
            {
                var page = server.Pull(Epoch, replica.Cursor, random.Next(1, 8));
                Assert.Equal(PullStatus.Ok, page.Status);
                Assert.NotEmpty(page.Changes);
                var checkpoint = replica.Checkpoint();

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
