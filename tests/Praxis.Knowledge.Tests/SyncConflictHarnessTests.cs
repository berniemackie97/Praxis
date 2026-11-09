using Xunit;
using static Praxis.Knowledge.Tests.SyncHarnessInputs;

namespace Praxis.Knowledge.Tests;

public sealed class SyncConflictHarnessTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public void SeededConcurrentRenameEditAndDeleteRetainLosingContributions(int seed)
    {
        SyncContractFixture server = new SyncContractFixture(Epoch);
        OfflineSyncReplica[] clients = new[]
        {
            OfflineSyncReplica.Start(Epoch, FirstDevice),
            OfflineSyncReplica.Start(Epoch, SecondDevice),
            OfflineSyncReplica.Start(Epoch, ThirdDevice)
        };
        SyncMutation created = NewMutation(FirstDevice, TestOperationId(1), SharedRecord, 0,
            SyncAction.Upsert, "original.md", "original");
        Assert.Equal(ApplyStatus.Applied, server.Apply(created).Status);
        foreach (OfflineSyncReplica client in clients)
        {
            client.CommitPage(server.Pull(Epoch, 0, 10));
        }

        Random random = new Random(seed);
        SyncMutation[] attempted = new[]
        {
            NewMutation(FirstDevice, NextGuid(random), SharedRecord, 1,
                SyncAction.Upsert, "renamed.md", "original"),
            NewMutation(SecondDevice, NextGuid(random), SharedRecord, 1,
                SyncAction.Upsert, "original.md", "offline edit"),
            NewMutation(ThirdDevice, NextGuid(random), SharedRecord, 1,
                SyncAction.Delete, null, null)
        };
        List<(OfflineSyncReplica First, SyncMutation Second)> deliveries = clients.Zip(attempted).ToList();
        foreach ((OfflineSyncReplica client, SyncMutation mutation) in deliveries)
        {
            client.Queue(mutation);
        }

        Shuffle(deliveries, random);
        int accepted = 0;
        foreach ((OfflineSyncReplica client, SyncMutation mutation) in deliveries)
        {
            SyncReply response = server.Apply(mutation);
            if (response.Status == ApplyStatus.Applied)
            {
                accepted++;
            }
            else
            {
                Assert.Equal(ApplyStatus.StaleRevision, response.Status);
            }

            client.Acknowledge(mutation, response);
        }

        Assert.Equal(1, accepted);
        Assert.Equal(2, server.HeadSequence);
        SyncConflict[] rejected = clients.SelectMany(client => client.Conflicts).ToArray();
        Assert.Equal(2, rejected.Length);
        Assert.Equal(2, rejected.Select(item => item.Mutation.OperationId).Distinct().Count());
        Assert.All(rejected, conflict => Assert.Equal(ApplyStatus.StaleRevision, conflict.Reply.Status));
        Assert.Empty(clients.SelectMany(client => client.Pending));

        SyncRecord current = Assert.Single(server.Snapshot().Records);
        SyncMutation resolution = NewMutation(SecondDevice, NextGuid(random), SharedRecord,
            current.Revision, current.Deleted ? SyncAction.Restore : SyncAction.Upsert,
            "resolved.md", "explicitly resolved");
        clients[1].Queue(resolution);
        SyncReply receipt = server.Apply(resolution);
        Assert.Equal(ApplyStatus.Applied, receipt.Status);
        clients[1].Acknowledge(resolution, receipt);

        foreach (OfflineSyncReplica client in clients)
        {
            client.CommitPage(server.Pull(Epoch, client.Cursor, 10));
            Assert.True(server.Snapshot().Records.SequenceEqual(client.Checkpoint().Records),
                $"Seed {seed}: conflicting device did not converge after explicit resolution.");
        }

        Assert.Equal("explicitly resolved", Assert.Single(server.Snapshot().Records).Body);
        Assert.Equal(2, clients.Sum(client => client.Conflicts.Count));
    }

    [Fact]
    public void ConcurrentSamePathCreatesPreserveRejectedOperationForResolution()
    {
        SyncContractFixture server = new SyncContractFixture(Epoch);
        OfflineSyncReplica first = OfflineSyncReplica.Start(Epoch, FirstDevice);
        OfflineSyncReplica second = OfflineSyncReplica.Start(Epoch, SecondDevice);
        SyncMutation a = NewMutation(FirstDevice, TestOperationId(1), SharedRecord, 0,
            SyncAction.Upsert, "shared.md", "A");
        SyncMutation b = NewMutation(SecondDevice, TestOperationId(2),
            Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff"), 0,
            SyncAction.Upsert, "SHARED.md", "B");
        first.Queue(a);
        second.Queue(b);

        SyncReply accepted = server.Apply(a);
        SyncReply conflict = server.Apply(b);
        first.Acknowledge(a, accepted);
        second.Acknowledge(b, conflict);

        Assert.Equal(ApplyStatus.PathCollision, conflict.Status);
        Assert.Equal("B", Assert.Single(second.Conflicts).Mutation.Body);
        Assert.Equal(1, server.HeadSequence);

        SyncMutation resolved = b with { OperationId = TestOperationId(3), Path = "second.md" };
        second.Queue(resolved);
        second.Acknowledge(resolved, server.Apply(resolved));

        first.CommitPage(server.Pull(Epoch, 0, 10));
        second.CommitPage(server.Pull(Epoch, 0, 10));
        Assert.Equal(server.Snapshot().Records.ToArray(), first.Checkpoint().Records.ToArray());
        Assert.Equal(first.Checkpoint().Records.ToArray(), second.Checkpoint().Records.ToArray());
        Assert.Single(second.Conflicts);
    }

}
