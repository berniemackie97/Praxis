using System.Security.Cryptography;
using Praxis.Knowledge;
using Xunit;

namespace Praxis.Knowledge.Tests;

public sealed class LocalAttachmentTransferStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "praxis-transfer-" + Guid.NewGuid().ToString("N"));
    private readonly LocalAttachmentTransferStore store;

    public LocalAttachmentTransferStoreTests()
    {
        store = new LocalAttachmentTransferStore(root);
    }

    [Fact]
    public void InterruptedTransferResumesFromOnDiskOffsetAndPublishesExactBytes()
    {
        var content = new byte[] { 0, 255, 1, 2, 0, 42, 19 };
        var digest = Hash(content);
        var transferId = TransferId(1);

        Assert.Equal(new AttachmentTransferStatus(digest, content.Length, 0),
            store.Begin(transferId, digest, content.Length));
        Assert.Equal(3, store.Append(transferId, 0, content.AsSpan(0, 3)));

        var reopened = new LocalAttachmentTransferStore(root);
        Assert.Equal(3, reopened.Status(transferId).Uploaded);
        Assert.Equal(3, reopened.Begin(transferId, digest, content.Length).Uploaded);
        Assert.Equal(content.Length, reopened.Append(transferId, 3, content.AsSpan(3)));

        Assert.Equal(digest, reopened.Commit(transferId));
        Assert.True(reopened.IsCommitted(digest, content.Length));
        Assert.Equal(content, reopened.ReadVerified(digest));
        Assert.False(File.Exists(StagingPath(transferId)));
    }

    [Fact]
    public void RepeatedAcknowledgedChunkCannotDuplicateOrAlterBytes()
    {
        var content = new byte[] { 1, 2, 3, 4, 5 };
        var transferId = TransferId(2);
        store.Begin(transferId, Hash(content), content.Length);
        Assert.Equal(3, store.Append(transferId, 0, content.AsSpan(0, 3)));

        Assert.Equal(3, store.Append(transferId, 0, content.AsSpan(0, 3)));
        Assert.Throws<InvalidDataException>(() => store.Append(transferId, 0, new byte[] { 1, 2, 9 }));
        Assert.Throws<InvalidOperationException>(() => store.Append(transferId, 2, content.AsSpan(2, 2)));
        Assert.Equal(3, store.Status(transferId).Uploaded);

        Assert.Equal(5, store.Append(transferId, 3, content.AsSpan(3)));
        Assert.Equal(Hash(content), store.Commit(transferId));
    }

    [Fact]
    public void LostCommitAcknowledgementCanCheckVerifiedPublishedContent()
    {
        var content = new byte[] { 20, 30, 40 };
        var digest = Hash(content);
        var transferId = TransferId(3);

        store.Begin(transferId, digest, content.Length);
        store.Append(transferId, 0, content);
        store.Commit(transferId);

        var restarted = new LocalAttachmentTransferStore(root);
        Assert.True(restarted.IsCommitted(digest, content.Length));
        Assert.Equal(content, restarted.ReadVerified(digest));
        Assert.Throws<FileNotFoundException>(() => restarted.Status(transferId));
    }

    [Fact]
    public void IncompleteTransferCannotBePublished()
    {
        var content = new byte[] { 1, 2, 3 };
        var digest = Hash(content);
        var transferId = TransferId(4);
        store.Begin(transferId, digest, content.Length);
        store.Append(transferId, 0, content.AsSpan(0, 1));

        Assert.Throws<InvalidOperationException>(() => store.Commit(transferId));
        Assert.False(store.IsCommitted(digest, content.Length));
        Assert.Equal(1, store.Status(transferId).Uploaded);
    }

    [Fact]
    public void WrongDigestRetainsUncommittedBytesForInspection()
    {
        var content = new byte[] { 1, 2, 3, 4 };
        var wrongDigest = Hash(new byte[] { 9, 8, 7, 6 });
        var transferId = TransferId(5);
        store.Begin(transferId, wrongDigest, content.Length);
        store.Append(transferId, 0, content);

        Assert.Throws<InvalidDataException>(() => store.Commit(transferId));
        Assert.False(store.IsCommitted(wrongDigest, content.Length));
        Assert.Equal(content.Length, store.Status(transferId).Uploaded);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "content")));
    }

    [Fact]
    public void StagedBytesChangedAfterAcknowledgementAreNotPublished()
    {
        var content = new byte[] { 1, 2, 3, 4 };
        var digest = Hash(content);
        var transferId = TransferId(6);
        store.Begin(transferId, digest, content.Length);
        store.Append(transferId, 0, content);

        using (var corrupted = new FileStream(StagingPath(transferId),
            FileMode.Open, FileAccess.Write, FileShare.None))
        {
            corrupted.Position = 40 + 2;
            corrupted.WriteByte(99);
        }

        Assert.Throws<InvalidDataException>(() => store.Commit(transferId));
        Assert.False(store.IsCommitted(digest, content.Length));
        Assert.Equal(content.Length, store.Status(transferId).Uploaded);
    }

    [Fact]
    public void CorruptPublishedContentIsRejectedAndNeverOverwrittenByAnotherTransfer()
    {
        var content = new byte[] { 1, 2, 3, 4 };
        var digest = Hash(content);
        var first = TransferId(7);
        store.Begin(first, digest, content.Length);
        store.Append(first, 0, content);
        store.Commit(first);

        File.WriteAllBytes(Path.Combine(root, "content", digest), [0, 2, 3, 4]);
        Assert.Throws<InvalidDataException>(() => store.ReadVerified(digest));
        Assert.Throws<InvalidDataException>(() => store.IsCommitted(digest, content.Length));

        var second = TransferId(8);
        store.Begin(second, digest, content.Length);
        store.Append(second, 0, content);
        Assert.Throws<InvalidDataException>(() => store.Commit(second));
        Assert.Equal(content.Length, store.Status(second).Uploaded);
        Assert.Equal([0, 2, 3, 4], File.ReadAllBytes(Path.Combine(root, "content", digest)));
    }

    [Fact]
    public void TwoAttachmentIdentitiesCanShareOneCommittedPayload()
    {
        var bytes = new byte[] { 5, 6, 7 };
        var first = AttachmentRecord.Create("assets/a.bin", bytes);
        var second = AttachmentRecord.Create("assets/b.bin", bytes);
        var digest = first.Sha256;
        var transferA = TransferId(9);
        var transferB = TransferId(10);

        store.Begin(transferA, digest, bytes.Length);
        store.Begin(transferB, digest, bytes.Length);
        store.Append(transferA, 0, bytes);
        store.Append(transferB, 0, bytes);
        Assert.Equal(digest, store.Commit(transferA));
        Assert.Equal(digest, store.Commit(transferB));

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(first.Id, first.Rename("images/renamed.bin").Id);
        Assert.Equal(bytes, store.ReadVerified(digest));
        Assert.Single(Directory.GetFiles(Path.Combine(root, "content")));
    }

    [Fact]
    public void EmptyContentIsACompleteVerifiableTransfer()
    {
        var digest = Hash([]);
        var transferId = TransferId(11);
        var status = store.Begin(transferId, digest, 0);

        Assert.Equal(0, status.Uploaded);
        Assert.Equal(digest, store.Commit(transferId));
        Assert.Empty(store.ReadVerified(digest));
        Assert.True(store.IsCommitted(digest, 0));
    }

    [Fact]
    public void ExistingTransferCannotBeReassignedToAnotherDigestOrLength()
    {
        var content = new byte[] { 1, 2 };
        var digest = Hash(content);
        var transferId = TransferId(12);
        store.Begin(transferId, digest, content.Length);
        store.Append(transferId, 0, content.AsSpan(0, 1));

        Assert.Throws<InvalidOperationException>(() => store.Begin(transferId, Hash([3, 4]), 2));
        Assert.Throws<InvalidOperationException>(() => store.Begin(transferId, digest, 1));
        Assert.Equal(1, store.Status(transferId).Uploaded);
    }

    [Fact]
    public void CorruptStagingHeaderFailsExplicitlyInsteadOfBeingReset()
    {
        var transferId = TransferId(13);
        File.WriteAllBytes(StagingPath(transferId), [0x00, 0x01]);

        Assert.Throws<InvalidDataException>(() => store.Status(transferId));
        Assert.Throws<InvalidDataException>(() => store.Begin(transferId, Hash([1]), 1));
        Assert.Equal([0x00, 0x01], File.ReadAllBytes(StagingPath(transferId)));
    }

    [Fact]
    public void MissingOrOversizedChunksAndOffsetsAreRejected()
    {
        var transferId = TransferId(14);
        var bytes = new byte[LocalAttachmentTransferStore.MaxChunkBytes + 1];
        store.Begin(transferId, Hash(bytes), bytes.Length);

        Assert.Throws<ArgumentOutOfRangeException>(() => store.Append(transferId, 0, []));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Append(transferId, -1, [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Append(transferId, 0, bytes));
        Assert.Throws<InvalidOperationException>(() => store.Append(transferId, 1, [1]));
        Assert.Equal(0, store.Status(transferId).Uploaded);
    }

    [Fact]
    public void InvalidIdsDigestsAndSizesAreRejectedBeforeCreatingStagingData()
    {
        var sha = Hash([1]);
        Assert.Throws<ArgumentException>(() => store.Begin(Guid.Empty, sha, 1));
        Assert.Throws<ArgumentException>(() => store.Begin(TransferId(15), "../escape", 1));
        Assert.Throws<ArgumentException>(() => store.Begin(TransferId(15), sha.ToUpperInvariant(), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.Begin(TransferId(15), sha, LocalAttachmentTransferStore.MaxAttachmentBytes + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Begin(TransferId(15), sha, -1));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "staging")));
    }

    [Fact]
    public void ClientCanResumeAfterPartialWriteByReReadingPhysicalOffset()
    {
        var content = new byte[] { 10, 11, 12, 13 };
        var digest = Hash(content);
        var transferId = TransferId(16);
        store.Begin(transferId, digest, content.Length);
        store.Append(transferId, 0, content.AsSpan(0, 2));

        using (var stream = new FileStream(StagingPath(transferId),
            FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = 42;
            stream.WriteByte(content[2]);
        }

        var restarted = new LocalAttachmentTransferStore(root);
        Assert.Equal(3, restarted.Status(transferId).Uploaded);
        Assert.Throws<InvalidOperationException>(() =>
            restarted.Append(transferId, 0, content));
        Assert.Equal(4, restarted.Append(transferId, 3, content.AsSpan(3)));
        Assert.Equal(digest, restarted.Commit(transferId));
    }

    private string StagingPath(Guid transferId)
        => Path.Combine(root, "staging", $"{transferId:N}.part");

    private static Guid TransferId(int number)
        => Guid.Parse($"aaaaaaaa-bbbb-4ccc-8ddd-{number:D12}");

    private static string Hash(ReadOnlySpan<byte> bytes)
        => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public void Dispose()
    {
        Directory.Delete(root, recursive: true);
    }
}
