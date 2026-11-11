using System.Security.Cryptography;
using Praxis.Infrastructure.Persistence.Sync;
using Praxis.Infrastructure.Security.Cryptography;
using Praxis.Notes.Journal;
using Xunit;

namespace Praxis.Infrastructure.Tests.Sync;

public sealed class SealedNoteOutboxTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "praxis-sealed-outbox-" + Guid.NewGuid().ToString("N"));

    public SealedNoteOutboxTests()
    {
        Directory.CreateDirectory(root);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(root, UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    [Fact]
    public void RequestCiphertextIsPersistedOnceAndCannotBeChangedBeforeOrAfterRestart()
    {
        Guid space = Guid.NewGuid();
        Guid epoch = Guid.NewGuid();
        Guid device = Guid.NewGuid();
        Guid record = Guid.NewGuid();
        byte[] key = ClientSpaceKeys.GenerateContentKey();
        PendingNoteEdit edit = new(Guid.NewGuid(), record, 0, LocalNoteAction.Upsert,
            "notes/hidden.md", "sensitive body must not appear on disk");
        SealedNoteOutbox first = new(root, space, epoch, device, 1);
        OpaqueSyncMutation sealedRequest = first.GetOrSeal(edit, space, epoch, device, 1, key, []);
        string path = Path.Combine(root, edit.OperationId.ToString("N") + ".json");
        byte[] before = File.ReadAllBytes(path);

        Assert.DoesNotContain("sensitive body must not appear on disk",
            System.Text.Encoding.UTF8.GetString(before));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(path));
        }

        SealedNoteOutbox reopened = new(root, space, epoch, device, 1);
        OpaqueSyncMutation retried = reopened.GetOrSeal(edit, space, epoch, device, 1, key, []);
        Assert.Equal(sealedRequest with { Ciphertext = retried.Ciphertext }, retried);
        Assert.Equal(sealedRequest.Ciphertext, retried.Ciphertext);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Throws<InvalidDataException>(() => reopened.GetOrSeal(edit with
        {
            Markdown = "changed under the same operation key"
        }, space, epoch, device, 1, key, []));
        Assert.ThrowsAny<CryptographicException>(() => reopened.GetOrSeal(
            edit, space, epoch, device, 1, ClientSpaceKeys.GenerateContentKey(), []));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public void AuthenticatedDeletionRetainsPriorContentOnlyInsideCiphertext()
    {
        Guid space = Guid.NewGuid();
        Guid epoch = Guid.NewGuid();
        Guid device = Guid.NewGuid();
        Guid record = Guid.NewGuid();
        byte[] key = ClientSpaceKeys.GenerateContentKey();
        LocalNoteState predecessor = new(record, 1, "notes/deleted.md", "previous private bytes", false);
        PendingNoteEdit edit = new(Guid.NewGuid(), record, 1, LocalNoteAction.Delete, null, null);
        OpaqueSyncMutation mutation = new SealedNoteOutbox(root, space, epoch, device, 1).GetOrSeal(
            edit, space, epoch, device, 1, key, [predecessor]);

        Assert.Equal(OpaqueSyncAction.Delete, mutation.Action);
        Assert.NotEmpty(mutation.Ciphertext);
        Assert.Equal(new OpaqueNoteContent(predecessor.Path, predecessor.Markdown),
            ClientOpaqueNoteCipher.Decrypt(key, space, epoch, record, 2,
                OpaqueSyncAction.Delete, 1, mutation.Ciphertext));
        Assert.ThrowsAny<CryptographicException>(() => ClientOpaqueNoteCipher.Decrypt(
            key, space, epoch, record, 2, OpaqueSyncAction.Upsert, 1, mutation.Ciphertext));
    }

    [Fact]
    public void CompletedOperationsArePrunedButPendingCiphertextIsPreserved()
    {
        Guid space = Guid.NewGuid();
        Guid epoch = Guid.NewGuid();
        Guid device = Guid.NewGuid();
        byte[] key = ClientSpaceKeys.GenerateContentKey();
        PendingNoteEdit a = new(Guid.NewGuid(), Guid.NewGuid(), 0,
            LocalNoteAction.Upsert, "a.md", "A");
        PendingNoteEdit b = new(Guid.NewGuid(), Guid.NewGuid(), 0,
            LocalNoteAction.Upsert, "b.md", "B");
        SealedNoteOutbox outbox = new(root, space, epoch, device, 1);
        outbox.GetOrSeal(a, space, epoch, device, 1, key, []);
        outbox.GetOrSeal(b, space, epoch, device, 1, key, []);

        outbox.Prune([b]);
        Assert.False(File.Exists(Path.Combine(root, a.OperationId.ToString("N") + ".json")));
        Assert.True(File.Exists(Path.Combine(root, b.OperationId.ToString("N") + ".json")));
        outbox.Prune([]);
        Assert.Empty(Directory.EnumerateFiles(root, "*.json")
            .Where(path => Path.GetFileName(path) != "outbox-context.json"));
    }

    [Fact]
    public void AnotherDeviceCannotReuseOrPruneTheExistingSealedOutbox()
    {
        Guid space = Guid.NewGuid();
        Guid epoch = Guid.NewGuid();
        Guid device = Guid.NewGuid();
        PendingNoteEdit edit = new(Guid.NewGuid(), Guid.NewGuid(), 0,
            LocalNoteAction.Upsert, "notes/a.md", "owner private");
        SealedNoteOutbox first = new(root, space, epoch, device, 1);
        first.GetOrSeal(edit, space, epoch, device, 1,
            ClientSpaceKeys.GenerateContentKey(), []);
        string sealedPath = Path.Combine(root, edit.OperationId.ToString("N") + ".json");
        byte[] original = File.ReadAllBytes(sealedPath);

        Assert.Throws<InvalidDataException>(() =>
            new SealedNoteOutbox(root, space, epoch, Guid.NewGuid(), 1));
        Assert.Throws<InvalidDataException>(() =>
            new SealedNoteOutbox(root, space, epoch, device, 2));
        Assert.Equal(original, File.ReadAllBytes(sealedPath));

        File.WriteAllText(Path.Combine(root, "unrelated.json"), "keep this file");
        Assert.Throws<InvalidDataException>(() => first.Prune([]));
        Assert.True(File.Exists(Path.Combine(root, "unrelated.json")));
        Assert.Equal(original, File.ReadAllBytes(sealedPath));
    }

    [Fact]
    public void OutboxRefusesPublicOrLinkedDirectories()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string linked = root + "-link";
        try
        {
            Directory.CreateSymbolicLink(linked, root);
            Assert.Throws<InvalidDataException>(() => new SealedNoteOutbox(linked,
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1));

            File.SetUnixFileMode(root, UnixFileMode.UserRead |
                UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);
            Assert.Throws<UnauthorizedAccessException>(() => new SealedNoteOutbox(root,
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1));
        }
        finally
        {
            if (Directory.Exists(linked))
            {
                Directory.Delete(linked);
            }
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
