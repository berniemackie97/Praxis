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
            store.Submit(writer, valid with { Action = OpaqueSyncAction.Delete }).Status);

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
                OperationId = Guid.NewGuid(), ExpectedRevision = 1, ContentEpoch = 2
            }).Status);
        Assert.Equal(OpaqueSyncStatus.ResetRequired,
            store.Pull(rotatedReader,
                new OpaqueSyncPull(1, space, epoch, device, 2, 0, 100)).Status);
        Assert.Equal(1, store.Pull(Scope(device, space, SecurityPermission.Read),
            new OpaqueSyncPull(1, space, epoch, device, 1, 0, 100)).HeadSequence);
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
