using System.Security.Cryptography;
using Praxis.Domain.Security;
using Praxis.Infrastructure.Persistence.Security.Registry;
using Praxis.Infrastructure.Persistence.Sync;
using Praxis.Infrastructure.Tests.Support;
using Xunit;

namespace Praxis.Infrastructure.Tests.Sync;

public sealed class OpaqueSyncStoreTests : IDisposable
{
    private readonly string root = Path.Combine(TestPaths.Temp,
        "praxis-sync-" + Guid.NewGuid().ToString("N"));

    public OpaqueSyncStoreTests()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void ExactReceiptAndOpaqueOrderedPagesSurviveProcessRestart()
    {
        Guid space = Guid.NewGuid();
        Guid epoch = Guid.NewGuid();
        Guid firstDevice = Guid.NewGuid();
        Guid secondDevice = Guid.NewGuid();
        Guid record = Guid.NewGuid();
        byte[] opaque = RandomNumberGenerator.GetBytes(48);
        OpaqueSyncMutation first = Request(space, epoch, firstDevice,
            Guid.NewGuid(), record, 0, OpaqueSyncAction.Upsert, opaque);
        OpaqueSyncReceipt original;
        using (OpaqueSyncStore store = OpaqueSyncStore.Open(root))
        {
            store.Provision(space, epoch);
            AuthorizedSpaceScope writer = Scope(firstDevice, space, SecurityPermission.Write);
            original = store.Submit(writer, first);
            Assert.Equal(OpaqueSyncStatus.Applied, original.Status);
            Assert.Equal(1, original.Sequence);
            Assert.Equal(1, original.Revision);

            Assert.Equal(original, store.Submit(writer, first));
            OpaqueSyncReceipt altered = store.Submit(writer,
                first with { Ciphertext = RandomNumberGenerator.GetBytes(48) });
            Assert.Equal(OpaqueSyncStatus.OperationKeyReused, altered.Status);
            Assert.Equal(original.Sequence, altered.Sequence);

            OpaqueSyncPage before = store.Pull(
                Scope(secondDevice, space, SecurityPermission.Read),
                new OpaqueSyncPull(1, space, epoch, secondDevice, 1, 0, 100));
            Assert.Equal(OpaqueSyncStatus.Ok, before.Status);
            Assert.Single(before.Changes);
            Assert.Equal(opaque, before.Changes[0].Ciphertext);
            Assert.False(before.Changes[0].Deleted);
            Assert.Equal(1, before.NextCursor);
            Assert.Equal(1, before.HeadSequence);
            Assert.Throws<IOException>(() => OpaqueSyncStore.Open(root));
        }

        using (OpaqueSyncStore restored = OpaqueSyncStore.Open(root))
        {
            Assert.Equal(original, restored.Submit(
                Scope(firstDevice, space, SecurityPermission.Write), first));
            OpaqueSyncPage after = restored.Pull(
                Scope(secondDevice, space, SecurityPermission.Read),
                new OpaqueSyncPull(1, space, epoch, secondDevice, 1, 0, 1));
            Assert.Single(after.Changes);
            Assert.Equal(opaque, after.Changes[0].Ciphertext);
            Assert.Equal(1, after.NextCursor);
            Assert.Equal(1, after.HeadSequence);
            Assert.Empty(restored.Pull(Scope(secondDevice, space, SecurityPermission.Read),
                new OpaqueSyncPull(1, space, epoch, secondDevice, 1, 1, 100)).Changes);
        }
    }

    [Fact]
    public void TwoDevicesConflictWithoutOverwriteOrSequenceGap()
    {
        using OpaqueSyncStore store = OpaqueSyncStore.Open(root);
        Guid space = Guid.NewGuid();
        Guid epoch = Guid.NewGuid();
        Guid first = Guid.NewGuid();
        Guid second = Guid.NewGuid();
        Guid record = Guid.NewGuid();
        store.Provision(space, epoch);
        AuthorizedSpaceScope firstWrite = Scope(first, space, SecurityPermission.Write);
        AuthorizedSpaceScope secondWrite = Scope(second, space, SecurityPermission.Write);
        OpaqueSyncMutation initial = Request(space, epoch, first, Guid.NewGuid(),
            record, 0, OpaqueSyncAction.Upsert);
        Assert.Equal(OpaqueSyncStatus.Applied, store.Submit(firstWrite, initial).Status);

        OpaqueSyncMutation remote = Request(space, epoch, second, Guid.NewGuid(),
            record, 1, OpaqueSyncAction.Upsert);
        Assert.Equal(2, store.Submit(secondWrite, remote).Sequence);
        OpaqueSyncMutation stale = Request(space, epoch, first, Guid.NewGuid(),
            record, 1, OpaqueSyncAction.Upsert);
        OpaqueSyncReceipt conflict = store.Submit(firstWrite, stale);
        Assert.Equal(OpaqueSyncStatus.StaleRevision, conflict.Status);
        Assert.Equal(2, conflict.Sequence);
        Assert.Equal(2, conflict.Revision);

        OpaqueSyncMutation delete = Request(space, epoch, first, Guid.NewGuid(),
            record, 2, OpaqueSyncAction.Delete, []);
        Assert.Equal(3, store.Submit(firstWrite, delete).Sequence);
        Assert.Equal(OpaqueSyncStatus.StaleRevision,
            store.Submit(secondWrite, Request(space, epoch, second, Guid.NewGuid(),
                record, 3, OpaqueSyncAction.Upsert)).Status);

        OpaqueSyncMutation restore = Request(space, epoch, second, Guid.NewGuid(),
            record, 3, OpaqueSyncAction.Restore);
        Assert.Equal(4, store.Submit(secondWrite, restore).Sequence);
        OpaqueSyncPage page = store.Pull(Scope(first, space, SecurityPermission.Read),
            new OpaqueSyncPull(1, space, epoch, first, 1, 0, 2));
        Assert.Equal(new long[] { 1, 2 }, page.Changes.Select(x => x.Sequence));
        Assert.Equal(4, page.HeadSequence);
        Assert.Equal(2, page.NextCursor);
        OpaqueSyncPage remaining = store.Pull(Scope(first, space, SecurityPermission.Read),
            new OpaqueSyncPull(1, space, epoch, first, 1, page.NextCursor, 2));
        Assert.Equal(new long[] { 3, 4 }, remaining.Changes.Select(x => x.Sequence));
        Assert.True(remaining.Changes[0].Deleted);
        Assert.False(remaining.Changes[1].Deleted);
    }

    [Fact]
    public void WrongIdentityBoundsAndEpochRefuseWithoutMutation()
    {
        using OpaqueSyncStore store = OpaqueSyncStore.Open(root);
        Guid space = Guid.NewGuid();
        Guid epoch = Guid.NewGuid();
        Guid device = Guid.NewGuid();
        Guid record = Guid.NewGuid();
        store.Provision(space, epoch);
        AuthorizedSpaceScope writer = Scope(device, space, SecurityPermission.Write);
        AuthorizedSpaceScope reader = Scope(device, space, SecurityPermission.Read);
        OpaqueSyncMutation valid = Request(space, epoch, device, Guid.NewGuid(),
            record, 0, OpaqueSyncAction.Upsert);
        Assert.Throws<UnauthorizedAccessException>(() =>
            store.Submit(reader, valid));
        Assert.Throws<UnauthorizedAccessException>(() =>
            store.Pull(writer, new OpaqueSyncPull(1, space, epoch, device, 1, 0, 100)));
        Assert.Throws<UnauthorizedAccessException>(() =>
            store.Submit(writer, valid with { DeviceId = Guid.NewGuid() }));
        Assert.Throws<UnauthorizedAccessException>(() =>
            store.Submit(writer, valid with { ContentEpoch = 2 }));
        Assert.Equal(OpaqueSyncStatus.WrongCollectionEpoch,
            store.Submit(writer, valid with { CollectionEpoch = Guid.NewGuid() }).Status);
        Assert.Equal(OpaqueSyncStatus.UnsupportedProtocolVersion,
            store.Submit(writer, valid with { ProtocolVersion = 2 }).Status);
        Assert.Equal(OpaqueSyncStatus.InvalidRequest,
            store.Submit(writer, valid with { Ciphertext = new byte[27] }).Status);
        Assert.Equal(OpaqueSyncStatus.InvalidRequest,
            store.Submit(writer, valid with { Ciphertext = new byte[65537] }).Status);
        Assert.Equal(OpaqueSyncStatus.InvalidRequest,
            store.Submit(writer, valid with { OperationId = Guid.Empty }).Status);
        Assert.Equal(OpaqueSyncStatus.InvalidRequest,
            store.Submit(writer, valid with { Action = OpaqueSyncAction.Delete, Ciphertext = [1] }).Status);

        Assert.Equal(OpaqueSyncStatus.WrongCollectionEpoch,
            store.Pull(reader, new OpaqueSyncPull(1, space, Guid.NewGuid(), device, 1, 0, 100)).Status);
        Assert.Equal(OpaqueSyncStatus.UnsupportedProtocolVersion,
            store.Pull(reader, new OpaqueSyncPull(2, space, epoch, device, 1, 0, 100)).Status);
        Assert.Equal(OpaqueSyncStatus.InvalidCursor,
            store.Pull(reader, new OpaqueSyncPull(1, space, epoch, device, 1, 1, 100)).Status);
        Assert.Equal(OpaqueSyncStatus.InvalidCursor,
            store.Pull(reader, new OpaqueSyncPull(1, space, epoch, device, 1, 0, 101)).Status);
        Assert.Equal(0, store.Pull(reader,
            new OpaqueSyncPull(1, space, epoch, device, 1, 0, 100)).HeadSequence);
    }

    [Fact]
    public void RecordIdentityCannotMoveAcrossSpaces()
    {
        using OpaqueSyncStore store = OpaqueSyncStore.Open(root);
        Guid firstSpace = Guid.NewGuid();
        Guid secondSpace = Guid.NewGuid();
        Guid epoch = Guid.NewGuid();
        Guid record = Guid.NewGuid();
        Guid device = Guid.NewGuid();
        store.Provision(firstSpace, epoch);
        store.Provision(secondSpace, epoch);
        Assert.Equal(OpaqueSyncStatus.Applied,
            store.Submit(Scope(device, firstSpace, SecurityPermission.Write),
                Request(firstSpace, epoch, device, Guid.NewGuid(),
                    record, 0, OpaqueSyncAction.Upsert)).Status);

        Assert.Equal(OpaqueSyncStatus.InvalidRequest,
            store.Submit(Scope(device, secondSpace, SecurityPermission.Write),
                Request(secondSpace, epoch, device, Guid.NewGuid(),
                    record, 0, OpaqueSyncAction.Upsert)).Status);
        Assert.Empty(store.Pull(Scope(device, secondSpace, SecurityPermission.Read),
            new OpaqueSyncPull(1, secondSpace, epoch, device, 1, 0, 100)).Changes);
        Assert.Throws<InvalidOperationException>(() =>
            store.Provision(firstSpace, Guid.NewGuid()));
    }

    [Fact]
    public void ContentKeyRotationRequiresExplicitEncryptedRebootstrap()
    {
        using OpaqueSyncStore store = OpaqueSyncStore.Open(root);
        Guid space = Guid.NewGuid();
        Guid epoch = Guid.NewGuid();
        Guid device = Guid.NewGuid();
        Guid record = Guid.NewGuid();
        store.Provision(space, epoch);
        AuthorizedSpaceScope writer = Scope(device, space, SecurityPermission.Write);
        OpaqueSyncMutation initial = Request(space, epoch, device, Guid.NewGuid(),
            record, 0, OpaqueSyncAction.Upsert);
        Assert.Equal(OpaqueSyncStatus.Applied, store.Submit(writer, initial).Status);

        AuthorizedSpaceScope rotatedWriter = new(Guid.NewGuid(), device, space,
            SecurityPermission.Write, 2);
        AuthorizedSpaceScope rotatedReader = new(Guid.NewGuid(), device, space,
            SecurityPermission.Read, 2);
        Assert.Equal(OpaqueSyncStatus.ResetRequired,
            store.Submit(rotatedWriter, initial with
            {
                OperationId = Guid.NewGuid(),
                ExpectedRevision = 1,
                ContentEpoch = 2
            }).Status);
        Assert.Equal(OpaqueSyncStatus.ResetRequired,
            store.Pull(rotatedReader,
                new OpaqueSyncPull(1, space, epoch, device, 2, 0, 100)).Status);
        Assert.Equal(1, store.Pull(Scope(device, space, SecurityPermission.Read),
            new OpaqueSyncPull(1, space, epoch, device, 1, 0, 100)).HeadSequence);
    }

    [Fact]
    public void FailedEventInsertRollsBackRecordAndReceiptTogether()
    {
        using OpaqueSyncStore store = OpaqueSyncStore.Open(root);
        Guid space = Guid.NewGuid();
        Guid epoch = Guid.NewGuid();
        Guid device = Guid.NewGuid();
        Guid record = Guid.NewGuid();
        store.Provision(space, epoch);
        using Microsoft.Data.Sqlite.SqliteConnection fault = new(
            $"Data Source={Path.Combine(root, "sync.sqlite")};Pooling=False");
        fault.Open();
        using (Microsoft.Data.Sqlite.SqliteCommand trigger = fault.CreateCommand())
        {
            trigger.CommandText = """
                CREATE TRIGGER fail_sync_event BEFORE INSERT ON sync_events
                BEGIN SELECT RAISE(ABORT, 'synthetic fault'); END;
                """;
            trigger.ExecuteNonQuery();
        }

        OpaqueSyncMutation mutation = Request(space, epoch, device, Guid.NewGuid(),
            record, 0, OpaqueSyncAction.Upsert);
        AuthorizedSpaceScope writer = Scope(device, space, SecurityPermission.Write);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
            store.Submit(writer, mutation));

        using (Microsoft.Data.Sqlite.SqliteCommand drop = fault.CreateCommand())
        {
            drop.CommandText = "DROP TRIGGER fail_sync_event";
            drop.ExecuteNonQuery();
        }

        OpaqueSyncPage empty = store.Pull(Scope(device, space, SecurityPermission.Read),
            new OpaqueSyncPull(1, space, epoch, device, 1, 0, 100));
        Assert.Empty(empty.Changes);
        Assert.Equal(0, empty.HeadSequence);
        Assert.Equal(OpaqueSyncStatus.Applied, store.Submit(writer, mutation).Status);
        Assert.Equal(1, store.Pull(Scope(device, space, SecurityPermission.Read),
            new OpaqueSyncPull(1, space, epoch, device, 1, 0, 100)).HeadSequence);
    }

    [Fact]
    public void PrivateRootAndSchemaVersionAreRequired()
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute | UnixFileMode.GroupRead);
            Assert.Throws<UnauthorizedAccessException>(() => OpaqueSyncStore.Open(root));
            File.SetUnixFileMode(root, UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute);
        }

        using (OpaqueSyncStore created = OpaqueSyncStore.Open(root))
        {
            created.Provision(Guid.NewGuid(), Guid.NewGuid());
        }

        using Microsoft.Data.Sqlite.SqliteConnection corrupt = new(
            $"Data Source={Path.Combine(root, "sync.sqlite")};Pooling=False");
        corrupt.Open();
        using (Microsoft.Data.Sqlite.SqliteCommand future = corrupt.CreateCommand())
        {
            future.CommandText = "PRAGMA user_version=999";
            future.ExecuteNonQuery();
        }

        corrupt.Close();
        Assert.Throws<NotSupportedException>(() => OpaqueSyncStore.Open(root));
    }

    [Fact]
    public void PrunedHistoryUsesStableOpaqueSnapshotPagesAndRetainsReceipts()
    {
        using OpaqueSyncStore store = OpaqueSyncStore.Open(root);
        Guid space = Guid.NewGuid(), epoch = Guid.NewGuid(), device = Guid.NewGuid();
        store.Provision(space, epoch);
        AuthorizedSpaceScope writer = Scope(device, space, SecurityPermission.Write);
        AuthorizedSpaceScope reader = Scope(device, space, SecurityPermission.Read);
        Guid[] recordIds = Enumerable.Range(0, 18).Select(_ => Guid.NewGuid()).ToArray();
        OpaqueSyncMutation[] edits = recordIds.Select(id =>
            Request(space, epoch, device, Guid.NewGuid(), id, 0, OpaqueSyncAction.Upsert))
            .ToArray();
        foreach (OpaqueSyncMutation mutation in edits)
        {
            Assert.Equal(OpaqueSyncStatus.Applied, store.Submit(writer, mutation).Status);
        }

        store.PruneThrough(space, 17);
        Assert.Equal(OpaqueSyncStatus.CursorExpired,
            store.Pull(reader, new OpaqueSyncPull(1, space, epoch, device, 1, 0, 100)).Status);
        OpaqueSyncSnapshotPage first = store.Snapshot(reader,
            new OpaqueSyncSnapshotPull(1, space, epoch, device, 1, 0, Guid.Empty, 16));
        Assert.Equal(OpaqueSyncStatus.Ok, first.Status);
        Assert.Equal(18, first.HeadSequence);
        Assert.Equal(18, first.TotalRecords);
        Assert.Equal(16, first.Records.Count);
        Assert.True(recordIds.ToHashSet().SetEquals(
            first.Records.Select(record => record.RecordId)
                .Concat(store.Snapshot(reader, new OpaqueSyncSnapshotPull(
                    1, space, epoch, device, 1, first.HeadSequence,
                    first.Records[^1].RecordId, 16)).Records.Select(record => record.RecordId))));
        Assert.Equal(18, store.Snapshot(reader, new OpaqueSyncSnapshotPull(
            1, space, epoch, device, 1, 0, Guid.Empty, 16)).TotalRecords);

        OpaqueSyncMutation added = Request(space, epoch, device,
            Guid.NewGuid(), Guid.NewGuid(), 0, OpaqueSyncAction.Upsert);
        Assert.Equal(OpaqueSyncStatus.Applied, store.Submit(writer, added).Status);
        OpaqueSyncSnapshotPage changed = store.Snapshot(reader,
            new OpaqueSyncSnapshotPull(1, space, epoch, device, 1, 18,
                first.Records[^1].RecordId, 16));
        Assert.Equal(OpaqueSyncStatus.InvalidCursor, changed.Status);
        Assert.Empty(changed.Records);
        Assert.Equal(0, changed.HeadSequence);
        Assert.Equal(0, changed.TotalRecords);

        Assert.Equal(OpaqueSyncStatus.Applied, store.Submit(writer, edits[0]).Status);
        Assert.Equal(OpaqueSyncStatus.CursorExpired,
            store.Pull(reader, new OpaqueSyncPull(1, space, epoch, device, 1, 16, 100)).Status);
    }

    [Fact]
    public void SnapshotReadRejectsForeignScopesAndIllegalBoundsWithoutPrivateMetadata()
    {
        using OpaqueSyncStore store = OpaqueSyncStore.Open(root);
        Guid space = Guid.NewGuid(), epoch = Guid.NewGuid(), device = Guid.NewGuid();
        store.Provision(space, epoch);
        AuthorizedSpaceScope reader = Scope(device, space, SecurityPermission.Read);
        OpaqueSyncSnapshotPull valid =
            new(1, space, epoch, device, 1, 0, Guid.Empty, 16);
        Assert.Throws<UnauthorizedAccessException>(() =>
            store.Snapshot(Scope(device, space, SecurityPermission.Write), valid));
        Assert.Throws<UnauthorizedAccessException>(() =>
            store.Snapshot(reader, valid with { DeviceId = Guid.NewGuid() }));
        Assert.Throws<UnauthorizedAccessException>(() =>
            store.Snapshot(reader, valid with { ContentEpoch = 2 }));
        foreach (OpaqueSyncSnapshotPull invalid in new[]
        {
            valid with { CollectionEpoch = Guid.NewGuid() },
            valid with { ProtocolVersion = 2 },
            valid with { Limit = 17 },
            valid with { ExpectedHead = -1 },
            valid with { AfterRecordId = Guid.NewGuid() }
        })
        {
            OpaqueSyncSnapshotPage rejected = store.Snapshot(reader, invalid);
            Assert.NotEqual(OpaqueSyncStatus.Ok, rejected.Status);
            Assert.Empty(rejected.Records);
            Assert.Equal(0, rejected.TotalRecords);
            Assert.Equal(0, rejected.HeadSequence);
        }
    }

    [Fact]
    public void SnapshotRefusesMixedKeyEpochsAndPreservesEncryptedProjection()
    {
        using OpaqueSyncStore store = OpaqueSyncStore.Open(root);
        Guid space = Guid.NewGuid(), epoch = Guid.NewGuid(), device = Guid.NewGuid();
        store.Provision(space, epoch);
        AuthorizedSpaceScope owner = Scope(device, space, SecurityPermission.Write);
        OpaqueSyncMutation mutation = Request(space, epoch, device,
            Guid.NewGuid(), Guid.NewGuid(), 0, OpaqueSyncAction.Upsert);
        Assert.Equal(OpaqueSyncStatus.Applied, store.Submit(owner, mutation).Status);

        AuthorizedSpaceScope newEpoch = new(Guid.NewGuid(), device, space,
            SecurityPermission.Read, 2);
        OpaqueSyncSnapshotPage denied = store.Snapshot(newEpoch,
            new OpaqueSyncSnapshotPull(1, space, epoch, device, 2, 0, Guid.Empty, 16));
        Assert.Equal(OpaqueSyncStatus.ResetRequired, denied.Status);
        Assert.Empty(denied.Records);
        Assert.Equal(0, denied.HeadSequence);
        Assert.Equal(0, denied.TotalRecords);
        Assert.Equal(mutation.Ciphertext, Assert.Single(store.Snapshot(
            Scope(device, space, SecurityPermission.Read),
            new OpaqueSyncSnapshotPull(1, space, epoch, device, 1, 0, Guid.Empty, 16)).Records)
            .Ciphertext);
    }

    [Fact]
    public void StagedEpochRotationRetainsOldProjectionUntilCompleteAtomicCommit()
    {
        Guid space = Guid.NewGuid(), epoch = Guid.NewGuid(), device = Guid.NewGuid();
        Guid rotation = Guid.NewGuid();
        OpaqueSyncMutation first = Request(space, epoch, device, Guid.NewGuid(),
            Guid.NewGuid(), 0, OpaqueSyncAction.Upsert);
        OpaqueSyncMutation second = Request(space, epoch, device, Guid.NewGuid(),
            Guid.NewGuid(), 0, OpaqueSyncAction.Upsert);
        OpaqueSyncRotationCommit commit = new(1, space, epoch, device,
            rotation, 2, 1, 2);
        OpaqueSyncSnapshotRecord sealedFirst =
            new(first.RecordId, 1, false, 2, RandomNumberGenerator.GetBytes(48));
        OpaqueSyncSnapshotRecord sealedSecond =
            new(second.RecordId, 1, false, 2, RandomNumberGenerator.GetBytes(48));
        AuthorizedSpaceScope newWriter = new(Guid.NewGuid(), device, space,
            SecurityPermission.Write, 2);
        AuthorizedSpaceScope newReader = new(Guid.NewGuid(), device, space,
            SecurityPermission.Read, 2);

        using (OpaqueSyncStore store = OpaqueSyncStore.Open(root))
        {
            store.Provision(space, epoch);
            AuthorizedSpaceScope oldWriter = Scope(device, space, SecurityPermission.Write);
            Assert.Equal(1, store.Submit(oldWriter, first).Sequence);
            Assert.Equal(2, store.Submit(oldWriter, second).Sequence);

            OpaqueSyncRotationStage stage = new(1, space, epoch, device,
                rotation, 2, 1, 2, sealedFirst);
            Assert.Equal(OpaqueSyncStatus.Ok, store.StageRotation(newWriter, stage).Status);
            Assert.Equal(OpaqueSyncStatus.InvalidRequest, store.CommitRotation(newWriter, commit).Status);
            Assert.Equal(OpaqueSyncStatus.ResetRequired,
                store.Snapshot(newReader, new OpaqueSyncSnapshotPull(
                    1, space, epoch, device, 2, 0, Guid.Empty, 16)).Status);
            Assert.Equal(first.Ciphertext, Assert.Single(store.Pull(
                Scope(device, space, SecurityPermission.Read),
                new OpaqueSyncPull(1, space, epoch, device, 1, 0, 1)).Changes).Ciphertext);
        }

        using (OpaqueSyncStore resumed = OpaqueSyncStore.Open(root))
        {
            Assert.Equal(OpaqueSyncStatus.Ok, resumed.StageRotation(newWriter,
                new OpaqueSyncRotationStage(1, space, epoch, device, rotation, 2, 1, 2,
                    sealedSecond)).Status);
            OpaqueSyncRotationReceipt completed = resumed.CommitRotation(newWriter, commit);
            Assert.Equal(OpaqueSyncStatus.Applied, completed.Status);
            Assert.Equal(2, completed.HeadSequence);
            Assert.Equal(completed.Status, resumed.CommitRotation(newWriter, commit).Status);
            Assert.Equal(OpaqueSyncStatus.CursorExpired, resumed.Pull(newReader,
                new OpaqueSyncPull(1, space, epoch, device, 2, 0, 100)).Status);
            OpaqueSyncSnapshotPage snapshot = resumed.Snapshot(newReader,
                new OpaqueSyncSnapshotPull(1, space, epoch, device, 2, 0, Guid.Empty, 16));
            Assert.Equal(OpaqueSyncStatus.Ok, snapshot.Status);
            Assert.Equal(2, snapshot.TotalRecords);
            Assert.Contains(snapshot.Records, item =>
                item.RecordId == first.RecordId && item.Ciphertext.SequenceEqual(sealedFirst.Ciphertext));
            Assert.Contains(snapshot.Records, item =>
                item.RecordId == second.RecordId && item.Ciphertext.SequenceEqual(sealedSecond.Ciphertext));
            Assert.Equal(OpaqueSyncStatus.ResetRequired, resumed.Pull(
                Scope(device, space, SecurityPermission.Read),
                new OpaqueSyncPull(1, space, epoch, device, 1, 0, 100)).Status);
            Assert.Equal(OpaqueSyncStatus.ResetRequired,
                resumed.Submit(Scope(device, space, SecurityPermission.Write), first).Status);
        }
    }

    [Fact]
    public void EpochRotationRejectsStaleHeadAndIncompleteProjectionWithoutMutation()
    {
        using OpaqueSyncStore store = OpaqueSyncStore.Open(root);
        Guid space = Guid.NewGuid(), epoch = Guid.NewGuid(), device = Guid.NewGuid();
        Guid rotation = Guid.NewGuid(), record = Guid.NewGuid();
        store.Provision(space, epoch);
        AuthorizedSpaceScope oldWriter = Scope(device, space, SecurityPermission.Write);
        AuthorizedSpaceScope newWriter = new(Guid.NewGuid(), device, space,
            SecurityPermission.Write, 2);
        OpaqueSyncMutation first = Request(space, epoch, device,
            Guid.NewGuid(), record, 0, OpaqueSyncAction.Upsert);
        Assert.Equal(OpaqueSyncStatus.Applied, store.Submit(oldWriter, first).Status);

        OpaqueSyncRotationStage staged = new(1, space, epoch, device,
            rotation, 1, 1, 2, new OpaqueSyncSnapshotRecord(record, 1, false, 2,
                RandomNumberGenerator.GetBytes(48)));
        Assert.Equal(OpaqueSyncStatus.Ok, store.StageRotation(newWriter, staged).Status);
        Assert.Throws<InvalidOperationException>(() => store.PruneThrough(space, 1));

        OpaqueSyncMutation added = Request(space, epoch, device,
            Guid.NewGuid(), Guid.NewGuid(), 0, OpaqueSyncAction.Upsert);
        Assert.Equal(2, store.Submit(oldWriter, added).Sequence);
        Assert.Equal(OpaqueSyncStatus.InvalidRequest, store.CommitRotation(newWriter,
            new OpaqueSyncRotationCommit(1, space, epoch, device,
                rotation, 1, 1, 2)).Status);
        Assert.Equal(2, store.Pull(Scope(device, space, SecurityPermission.Read),
            new OpaqueSyncPull(1, space, epoch, device, 1, 0, 100)).HeadSequence);
        Assert.Equal(OpaqueSyncStatus.Ok, store.Snapshot(
            Scope(device, space, SecurityPermission.Read),
            new OpaqueSyncSnapshotPull(1, space, epoch, device, 1, 0, Guid.Empty, 16)).Status);
    }

    [Fact]
    public void FailedRotationCommitRollsBackEveryCiphertextAndKeepsRetryableStaging()
    {
        using OpaqueSyncStore store = OpaqueSyncStore.Open(root);
        Guid space = Guid.NewGuid(), epoch = Guid.NewGuid(), device = Guid.NewGuid();
        Guid rotation = Guid.NewGuid();
        store.Provision(space, epoch);
        AuthorizedSpaceScope writer = Scope(device, space, SecurityPermission.Write);
        AuthorizedSpaceScope newWriter = new(Guid.NewGuid(), device, space,
            SecurityPermission.Write, 2);
        OpaqueSyncMutation first = Request(space, epoch, device,
            Guid.NewGuid(), Guid.NewGuid(), 0, OpaqueSyncAction.Upsert);
        OpaqueSyncMutation second = Request(space, epoch, device,
            Guid.NewGuid(), Guid.NewGuid(), 0, OpaqueSyncAction.Upsert);
        store.Submit(writer, first);
        store.Submit(writer, second);
        foreach (OpaqueSyncMutation original in new[] { first, second })
        {
            Assert.Equal(OpaqueSyncStatus.Ok, store.StageRotation(newWriter,
                new OpaqueSyncRotationStage(1, space, epoch, device,
                    rotation, 2, 1, 2, new OpaqueSyncSnapshotRecord(
                        original.RecordId, 1, false, 2,
                        RandomNumberGenerator.GetBytes(48)))).Status);
        }

        using Microsoft.Data.Sqlite.SqliteConnection fault = new(
            $"Data Source={Path.Combine(root, "sync.sqlite")};Pooling=False");
        fault.Open();
        using (Microsoft.Data.Sqlite.SqliteCommand create = fault.CreateCommand())
        {
            create.CommandText = """
                CREATE TRIGGER fail_rotation BEFORE UPDATE OF key_epoch ON sync_records
                BEGIN SELECT RAISE(ABORT, 'synthetic rotation fault'); END;
                """;
            create.ExecuteNonQuery();
        }

        OpaqueSyncRotationCommit commit = new(1, space, epoch, device,
            rotation, 2, 1, 2);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() =>
            store.CommitRotation(newWriter, commit));
        OpaqueSyncPage unchanged = store.Pull(
            Scope(device, space, SecurityPermission.Read),
            new OpaqueSyncPull(1, space, epoch, device, 1, 0, 100));
        Assert.Equal(2, unchanged.HeadSequence);
        Assert.Equal(first.Ciphertext, unchanged.Changes[0].Ciphertext);
        Assert.Equal(second.Ciphertext, unchanged.Changes[1].Ciphertext);

        using (Microsoft.Data.Sqlite.SqliteCommand drop = fault.CreateCommand())
        {
            drop.CommandText = "DROP TRIGGER fail_rotation";
            drop.ExecuteNonQuery();
        }

        Assert.Equal(OpaqueSyncStatus.Applied,
            store.CommitRotation(newWriter, commit).Status);
        Assert.Equal(OpaqueSyncStatus.CursorExpired,
            store.Pull(new AuthorizedSpaceScope(Guid.NewGuid(), device, space,
                SecurityPermission.Read, 2),
                new OpaqueSyncPull(1, space, epoch, device, 2, 0, 100)).Status);
    }

    private static AuthorizedSpaceScope Scope(Guid device, Guid space, SecurityPermission permission) =>
        new(Guid.NewGuid(), device, space, permission, 1);

    private static OpaqueSyncMutation Request(Guid space, Guid epoch, Guid device,
        Guid operation, Guid record, long revision, OpaqueSyncAction action, byte[]? ciphertext = null) =>
        new(1, space, epoch, device, operation, record, revision, action, 1,
            ciphertext ?? RandomNumberGenerator.GetBytes(48));

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
