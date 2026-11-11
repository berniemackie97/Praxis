using System.Security.Cryptography;
using Praxis.Notes.Attachments;
using Praxis.Notes.Model;
using Xunit;

namespace Praxis.Notes.Tests.Attachments;

public sealed class LocalAttachmentTransferStoreTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "praxis-transfer-" + Guid.NewGuid().ToString("N"));
    private readonly AttachmentTransferStore store;

    public LocalAttachmentTransferStoreTests()
    {
        store = new AttachmentTransferStore(root);
    }

    [Fact]
    public void InterruptedTransferResumesFromOnDiskOffsetAndPublishesExactBytes()
    {
        byte[] content = [0, 255, 1, 2, 0, 42, 19];
        string digest = Hash(content);
        Guid transferId = TransferId(1);

        Assert.Equal(new AttachmentTransferStatus(digest, content.Length, 0),
            store.Begin(transferId, digest, content.Length));
        Assert.Equal(3, store.Append(transferId, 0, content.AsSpan(0, 3)));

        AttachmentTransferStore reopened = new(root);
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
        byte[] content = [1, 2, 3, 4, 5];
        Guid transferId = TransferId(2);
        store.Begin(transferId, Hash(content), content.Length);
        Assert.Equal(3, store.Append(transferId, 0, content.AsSpan(0, 3)));

        Assert.Equal(3, store.Append(transferId, 0, content.AsSpan(0, 3)));
        Assert.Throws<InvalidDataException>(() => store.Append(transferId, 0, [1, 2, 9]));
        Assert.Throws<InvalidOperationException>(() => store.Append(transferId, 2, content.AsSpan(2, 2)));
        Assert.Equal(3, store.Status(transferId).Uploaded);

        Assert.Equal(5, store.Append(transferId, 3, content.AsSpan(3)));
        Assert.Equal(Hash(content), store.Commit(transferId));
    }

    [Fact]
    public void LostCommitAcknowledgementCanCheckVerifiedPublishedContent()
    {
        byte[] content = [20, 30, 40];
        string digest = Hash(content);
        Guid transferId = TransferId(3);

        store.Begin(transferId, digest, content.Length);
        store.Append(transferId, 0, content);
        store.Commit(transferId);

        AttachmentTransferStore restarted = new(root);
        Assert.True(restarted.IsCommitted(digest, content.Length));
        Assert.Equal(content, restarted.ReadVerified(digest));
        Assert.Throws<FileNotFoundException>(() => restarted.Status(transferId));
    }

    [Fact]
    public void IncompleteTransferCannotBePublished()
    {
        byte[] content = [1, 2, 3];
        string digest = Hash(content);
        Guid transferId = TransferId(4);
        store.Begin(transferId, digest, content.Length);
        store.Append(transferId, 0, content.AsSpan(0, 1));

        Assert.Throws<InvalidOperationException>(() => store.Commit(transferId));
        Assert.False(store.IsCommitted(digest, content.Length));
        Assert.Equal(1, store.Status(transferId).Uploaded);
    }

    [Fact]
    public void WrongDigestRetainsUncommittedBytesForInspection()
    {
        byte[] content = [1, 2, 3, 4];
        string wrongDigest = Hash([9, 8, 7, 6]);
        Guid transferId = TransferId(5);
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
        byte[] content = [1, 2, 3, 4];
        string digest = Hash(content);
        Guid transferId = TransferId(6);
        store.Begin(transferId, digest, content.Length);
        store.Append(transferId, 0, content);

        using (FileStream corrupted = new(StagingPath(transferId),
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
        byte[] content = [1, 2, 3, 4];
        string digest = Hash(content);
        Guid first = TransferId(7);
        store.Begin(first, digest, content.Length);
        store.Append(first, 0, content);
        store.Commit(first);

        File.WriteAllBytes(Path.Combine(root, "content", digest), [0, 2, 3, 4]);
        Assert.Throws<InvalidDataException>(() => store.ReadVerified(digest));
        Assert.Throws<InvalidDataException>(() => store.IsCommitted(digest, content.Length));

        Guid second = TransferId(8);
        store.Begin(second, digest, content.Length);
        store.Append(second, 0, content);
        Assert.Throws<InvalidDataException>(() => store.Commit(second));
        Assert.Equal(content.Length, store.Status(second).Uploaded);
        Assert.Equal([0, 2, 3, 4], File.ReadAllBytes(Path.Combine(root, "content", digest)));
    }

    [Fact]
    public void ExistingPublishedBlobDoesNotMaskCorruptedNewStagingBytes()
    {
        byte[] content = [1, 2, 3];
        string digest = Hash(content);
        Guid first = TransferId(17);
        store.Begin(first, digest, content.Length);
        store.Append(first, 0, content);
        store.Commit(first);

        Guid second = TransferId(18);
        store.Begin(second, digest, content.Length);
        store.Append(second, 0, [1, 2, 99]);

        Assert.Throws<InvalidDataException>(() => store.Commit(second));
        Assert.Equal(content, store.ReadVerified(digest));
        Assert.Equal(content.Length, store.Status(second).Uploaded);
    }

    [Fact]
    public void TwoAttachmentIdentitiesCanShareOneCommittedPayload()
    {
        byte[] bytes = [5, 6, 7];
        AttachmentRecord first = AttachmentRecord.Create("assets/a.bin", bytes);
        AttachmentRecord second = AttachmentRecord.Create("assets/b.bin", bytes);
        string digest = first.Sha256;
        Guid transferA = TransferId(9);
        Guid transferB = TransferId(10);

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
        string digest = Hash([]);
        Guid transferId = TransferId(11);
        AttachmentTransferStatus status = store.Begin(transferId, digest, 0);

        Assert.Equal(0, status.Uploaded);
        Assert.Equal(digest, store.Commit(transferId));
        Assert.Empty(store.ReadVerified(digest));
        Assert.True(store.IsCommitted(digest, 0));
    }

    [Fact]
    public void ExistingTransferCannotBeReassignedToAnotherDigestOrLength()
    {
        byte[] content = [1, 2];
        string digest = Hash(content);
        Guid transferId = TransferId(12);
        store.Begin(transferId, digest, content.Length);
        store.Append(transferId, 0, content.AsSpan(0, 1));

        Assert.Throws<InvalidOperationException>(() => store.Begin(transferId, Hash([3, 4]), 2));
        Assert.Throws<InvalidOperationException>(() => store.Begin(transferId, digest, 1));
        Assert.Equal(1, store.Status(transferId).Uploaded);
    }

    [Fact]
    public void CorruptStagingHeaderFailsExplicitlyInsteadOfBeingReset()
    {
        Guid transferId = TransferId(13);
        File.WriteAllBytes(StagingPath(transferId), [0x00, 0x01]);

        Assert.Throws<InvalidDataException>(() => store.Status(transferId));
        Assert.Throws<InvalidDataException>(() => store.Begin(transferId, Hash([1]), 1));
        Assert.Equal([0x00, 0x01], File.ReadAllBytes(StagingPath(transferId)));
    }

    [Fact]
    public void MissingOrOversizedChunksAndOffsetsAreRejected()
    {
        Guid transferId = TransferId(14);
        byte[] bytes = new byte[AttachmentTransferStore.MaxChunkBytes + 1];
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
        string sha = Hash([1]);
        Assert.Throws<ArgumentException>(() => store.Begin(Guid.Empty, sha, 1));
        Assert.Throws<ArgumentException>(() => store.Begin(TransferId(15), "../escape", 1));
        Assert.Throws<ArgumentException>(() => store.Begin(TransferId(15), sha.ToUpperInvariant(), 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            store.Begin(TransferId(15), sha, AttachmentTransferStore.MaxAttachmentBytes + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => store.Begin(TransferId(15), sha, -1));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "staging")));
    }

    [Fact]
    public void ClientCanResumeAfterPartialWriteByReReadingPhysicalOffset()
    {
        byte[] content = [10, 11, 12, 13];
        string digest = Hash(content);
        Guid transferId = TransferId(16);
        store.Begin(transferId, digest, content.Length);
        store.Append(transferId, 0, content.AsSpan(0, 2));

        using (FileStream stream = new(StagingPath(transferId),
            FileMode.Open, FileAccess.Write, FileShare.None))
        {
            stream.Position = 42;
            stream.WriteByte(content[2]);
        }

        AttachmentTransferStore restarted = new(root);
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
