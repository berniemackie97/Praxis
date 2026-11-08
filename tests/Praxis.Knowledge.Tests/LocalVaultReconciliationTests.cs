using System.Text;
using Praxis.Knowledge;
using Xunit;

namespace Praxis.Knowledge.Tests;

public sealed class LocalVaultReconciliationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "praxis-vault-update-" + Guid.NewGuid().ToString("N"));

    public LocalVaultReconciliationTests() => Directory.CreateDirectory(root);

    [Fact]
    public void UnchangedRescanRetainsIdentityAndDoesNotCreateRevisions()
    {
        var source = Vault();
        Put(source, "Notes/first.md", "# First\r\n");
        Put(source, "media/file.bin", "\0\ud7ff");
        var previous = LocalVaultExchange.Capture(source);

        var updated = LocalVaultReconciler.Reconcile(previous, LocalVaultExchange.Capture(source));

        Assert.Empty(updated.Changes);
        Assert.Equal(Assert.Single(previous.Notes).Id, Assert.Single(updated.Collection.Notes).Id);
        Assert.Equal(Assert.Single(previous.Notes).Revisions.ToArray(),
            Assert.Single(updated.Collection.Notes).Revisions.ToArray());
        Assert.Equal(Assert.Single(previous.Attachments).Id,
            Assert.Single(updated.Collection.Attachments).Id);
    }

    [Fact]
    public void UpdatedMarkdownAppendsExactlyOneHistoryRevisionWithStableIdentity()
    {
        var source = Vault();
        Put(source, "Notes/first.md", "# Before\n");
        var previous = LocalVaultExchange.Capture(source);
        Put(source, "Notes/first.md", "# After\r\n");

        var result = LocalVaultReconciler.Reconcile(previous, LocalVaultExchange.Capture(source));
        var note = Assert.Single(result.Collection.Notes);
        Assert.Equal(Assert.Single(previous.Notes).Id, note.Id);
        Assert.Equal(2, note.Revisions.Count);
        Assert.Equal("# Before\n", note.Revisions[0].Markdown);
        Assert.Equal("# After\r\n", note.Revisions[1].Markdown);
        Assert.Equal(note.Id, Assert.Single(result.Changes).RecordId);

        var unchanged = LocalVaultReconciler.Reconcile(result.Collection, LocalVaultExchange.Capture(source));
        Assert.Empty(unchanged.Changes);
        Assert.Equal(2, Assert.Single(unchanged.Collection.Notes).Revisions.Count);
    }

    [Fact]
    public void ChangingAttachmentBytesKeepsIdentityAndPreviousExplicitReferences()
    {
        var source = Vault();
        Put(source, "notes.md", "# Linked\n");
        Put(source, "media/photo.png", "\0\x01\x02");
        var captured = LocalVaultExchange.Capture(source);
        var note = Assert.Single(captured.Notes);
        var attachment = Assert.Single(captured.Attachments);
        var previous = new KnowledgeCollection(captured.Notes, captured.Attachments,
            [new AttachmentReference(note.Id, attachment.Id)]);
        Put(source, "media/photo.png", "\0\x01\x03");

        var result = LocalVaultReconciler.Reconcile(previous, LocalVaultExchange.Capture(source));
        var updated = Assert.Single(result.Collection.Attachments);
        Assert.Equal(attachment.Id, updated.Id);
        Assert.NotEqual(attachment.Sha256, updated.Sha256);
        Assert.Equal([0, 1, 3], updated.ReadContent());
        Assert.Equal(new AttachmentReference(note.Id, attachment.Id),
            Assert.Single(result.Collection.References));
        Assert.True(Assert.Single(result.Changes).ContentChanged);
        Assert.False(Assert.Single(result.Changes).PathChanged);
    }

    [Fact]
    public void ExplicitNoteRenameAndEditPreserveIdentityAndRevisionHistory()
    {
        var source = Vault();
        Put(source, "Old/first.md", "# Before");
        var previous = LocalVaultExchange.Capture(source);
        File.Delete(Path.Combine(source, "Old/first.md"));
        Directory.Delete(Path.Combine(source, "Old"));
        Put(source, "New/moved.md", "# After");
        var mapping = new[] { new VaultRename("Old/first.md", "New/moved.md") };

        var result = LocalVaultReconciler.Reconcile(previous, LocalVaultExchange.Capture(source),
            mapping);
        var updated = Assert.Single(result.Collection.Notes);
        Assert.Equal(Assert.Single(previous.Notes).Id, updated.Id);
        Assert.Equal(2, updated.Current.Number);
        Assert.Equal("Old/first.md", updated.Revisions[0].Path);
        Assert.Equal("New/moved.md", updated.Current.Path);
        Assert.Equal("# After", updated.Current.Markdown);
        Assert.True(Assert.Single(result.Changes).PathChanged);
        Assert.True(Assert.Single(result.Changes).ContentChanged);
    }

    [Fact]
    public void ExplicitBinaryRenameKeepsAttachmentIdentityAndAllLinks()
    {
        var source = Vault();
        Put(source, "note.md", "![[media/old.png]]");
        Put(source, "media/old.png", "\x01\x02\x03");
        var first = LocalVaultExchange.Capture(source);
        var previous = new KnowledgeCollection(first.Notes, first.Attachments,
            [new AttachmentReference(first.Notes[0].Id, first.Attachments[0].Id)]);
        File.Move(Path.Combine(source, "media/old.png"), Path.Combine(source, "media/new.png"));

        var updated = LocalVaultReconciler.Reconcile(previous, LocalVaultExchange.Capture(source),
            [new VaultRename("media/old.png", "media/new.png")]);
        Assert.Equal(first.Attachments[0].Id, updated.Collection.Attachments[0].Id);
        Assert.Equal(first.Attachments[0].Sha256, updated.Collection.Attachments[0].Sha256);
        Assert.Equal("media/new.png", updated.Collection.Attachments[0].Path);
        Assert.Equal(Assert.Single(previous.References), Assert.Single(updated.Collection.References));
        Assert.Equal("![[media/old.png]]", Assert.Single(updated.Collection.Notes).Current.Markdown);
        Assert.True(Assert.Single(updated.Changes).PathChanged);
    }

    [Fact]
    public void SwappedNamesCanBeResolvedWithoutGuessingIdentities()
    {
        var before = new KnowledgeCollection([
            NoteRecord.Create("one.md", "one"),
            NoteRecord.Create("two.md", "two")], [], []);
        var observed = new KnowledgeCollection([
            NoteRecord.Create("one.md", "two"),
            NoteRecord.Create("two.md", "one")], [], []);

        var result = LocalVaultReconciler.Reconcile(before, observed, [
            new VaultRename("one.md", "two.md"),
            new VaultRename("two.md", "one.md")]);

        var one = Assert.Single(result.Collection.Notes, note => note.Current.Path == "two.md");
        var two = Assert.Single(result.Collection.Notes, note => note.Current.Path == "one.md");
        Assert.Equal(before.Notes[0].Id, one.Id);
        Assert.Equal(before.Notes[1].Id, two.Id);
        Assert.Equal(2, one.Current.Number);
        Assert.Equal(2, two.Current.Number);
    }

    [Fact]
    public void CaseOnlyNoteRenamePreservesIdentityAndAppendsRevision()
    {
        var before = new KnowledgeCollection([NoteRecord.Create("Draft.md", "body")], [], []);
        var observed = new KnowledgeCollection([NoteRecord.Create("draft.md", "body")], [], []);

        var result = LocalVaultReconciler.Reconcile(before, observed);

        Assert.Equal(before.Notes[0].Id, Assert.Single(result.Collection.Notes).Id);
        Assert.Equal(2, result.Collection.Notes[0].Current.Number);
        Assert.True(Assert.Single(result.Changes).PathChanged);
        Assert.False(Assert.Single(result.Changes).ContentChanged);
    }

    [Fact]
    public void AddingFilesRetainsAllExistingIdentitiesAndReferences()
    {
        var source = Vault();
        Put(source, "base.md", "before");
        var previous = LocalVaultExchange.Capture(source);
        Put(source, "added.md", "new");
        Put(source, "photo.jpg", "\0\x06");

        var result = LocalVaultReconciler.Reconcile(previous, LocalVaultExchange.Capture(source));
        Assert.Equal(2, result.Collection.Notes.Count);
        Assert.Single(result.Collection.Attachments);
        Assert.Equal(previous.Notes[0].Id,
            Assert.Single(result.Collection.Notes, n => n.Current.Path == "base.md").Id);
        Assert.Equal(2, result.Changes.Count);
        Assert.All(result.Changes, change => Assert.Null(change.PreviousPath));
    }

    [Fact]
    public void MissingPriorFileFailsBeforeCreatingAnUpdatedArchive()
    {
        var source = Vault();
        Put(source, "original.md", "body");
        var original = Path.Combine(root, "original.zip");
        LocalVaultExchange.CaptureArchive(source, original);
        var snapshot = File.ReadAllBytes(original);
        File.Delete(Path.Combine(source, "original.md"));
        Put(source, "new.md", "body");
        var next = Path.Combine(root, "next.zip");

        var error = Assert.Throws<InvalidOperationException>(() =>
            LocalVaultExchange.UpdateArchive(source, original, next));
        Assert.Contains("original.md", error.Message);
        Assert.False(Path.Exists(next));
        Assert.Equal(snapshot, File.ReadAllBytes(original));
    }

    [Fact]
    public void ChangingFileTypeRequiresExplicitReview()
    {
        var before = new KnowledgeCollection([NoteRecord.Create("file.md", "# A")], [], []);
        var observed = new KnowledgeCollection([], [AttachmentRecord.Create("file.md", [1])], []);

        Assert.Throws<InvalidDataException>(() =>
            LocalVaultReconciler.Reconcile(before, observed));
    }

    [Fact]
    public void IdenticalBytePayloadDoesNotAutomaticallyImplyRename()
    {
        var before = new KnowledgeCollection([], [AttachmentRecord.Create("old.png", [1, 2])], []);
        var observed = new KnowledgeCollection([], [AttachmentRecord.Create("new.png", [1, 2])], []);

        Assert.Throws<InvalidOperationException>(() =>
            LocalVaultReconciler.Reconcile(before, observed));
    }

    [Fact]
    public void UnknownSourceOrMissingTargetMappingFails()
    {
        var before = new KnowledgeCollection([NoteRecord.Create("old.md", "old")], [], []);
        var observed = new KnowledgeCollection([NoteRecord.Create("new.md", "new")], [], []);

        Assert.Throws<InvalidDataException>(() => LocalVaultReconciler.Reconcile(before,
            observed, [new VaultRename("unknown.md", "new.md")]));
        Assert.Throws<InvalidDataException>(() => LocalVaultReconciler.Reconcile(before,
            observed, [new VaultRename("old.md", "absent.md")]));
    }

    [Fact]
    public void DuplicateOrConflictingRenamesFailWithoutUsingWrongIdentity()
    {
        var before = new KnowledgeCollection([
            NoteRecord.Create("one.md", "one"),
            NoteRecord.Create("two.md", "two")], [], []);
        var observed = new KnowledgeCollection([
            NoteRecord.Create("three.md", "changed"),
            NoteRecord.Create("four.md", "changed")], [], []);

        Assert.Throws<InvalidDataException>(() => LocalVaultReconciler.Reconcile(before,
            observed, [new VaultRename("one.md", "three.md"),
                new VaultRename("one.md", "four.md")]));
        Assert.Throws<InvalidDataException>(() => LocalVaultReconciler.Reconcile(before,
            observed, [new VaultRename("one.md", "three.md"),
                new VaultRename("two.md", "three.md")]));
    }

    [Fact]
    public void DangerousRenamePathIsRejected()
    {
        var before = new KnowledgeCollection([NoteRecord.Create("old.md", "old")], [], []);
        var observed = new KnowledgeCollection([NoteRecord.Create("new.md", "new")], [], []);

        Assert.Throws<ArgumentException>(() => LocalVaultReconciler.Reconcile(before,
            observed, [new VaultRename("old.md", "../escape.md")]));
    }

    [Fact]
    public void ReconcilerRejectsArbitraryObservedReferencesAndHistories()
    {
        var note = NoteRecord.Create("note.md", "first");
        var attachment = AttachmentRecord.Create("photo.png", [1]);
        var before = new KnowledgeCollection([note], [attachment],
            [new AttachmentReference(note.Id, attachment.Id)]);
        var captured = new KnowledgeCollection([note.Revise("note.md", "second")], [], []);

        Assert.Throws<ArgumentException>(() =>
            LocalVaultReconciler.Reconcile(before, captured));
        Assert.Throws<ArgumentException>(() =>
            LocalVaultReconciler.Reconcile(before, before));
    }

    [Fact]
    public void UpdateArchivePreservesPreviousHistoryAndRestoresTheLatestFiles()
    {
        var source = Vault();
        Put(source, "note.md", "v1\n");
        Put(source, "original.bin", "\0\x03");
        var firstPath = Path.Combine(root, "first.zip");
        LocalVaultExchange.CaptureArchive(source, firstPath);
        using var beforeStream = File.OpenRead(firstPath);
        var before = NoteArchive.ReadCollection(beforeStream);

        Put(source, "note.md", "v2\r\n");
        Put(source, "original.bin", "\0\x04");
        var secondPath = Path.Combine(root, "second.zip");
        var update = LocalVaultExchange.UpdateArchive(source, firstPath, secondPath);
        Assert.Equal(2, update.Changes.Count);

        using var stream = File.OpenRead(secondPath);
        var second = NoteArchive.ReadCollection(stream);
        Assert.Equal(before.Notes[0].Id, second.Notes[0].Id);
        Assert.Equal(2, second.Notes[0].Revisions.Count);
        Assert.Equal(before.Attachments[0].Id, second.Attachments[0].Id);
        Assert.NotEqual(before.Attachments[0].Sha256, second.Attachments[0].Sha256);

        var restored = Path.Combine(root, "restored");
        LocalVaultExchange.RestoreArchive(secondPath, restored);
        Assert.Equal("v2\r\n", File.ReadAllText(Path.Combine(restored, "note.md")));
        Assert.Equal([0, 4], File.ReadAllBytes(Path.Combine(restored, "original.bin")));
    }

    [Fact]
    public void ExistingTargetIsNotOverwrittenAndBaselineRemainsValid()
    {
        var source = Vault();
        Put(source, "note.md", "v1");
        var initial = Path.Combine(root, "initial.zip");
        LocalVaultExchange.CaptureArchive(source, initial);
        Put(source, "note.md", "v2");

        var destination = Path.Combine(root, "existing.zip");
        File.WriteAllBytes(destination, [42, 43]);
        Assert.Throws<IOException>(() =>
            LocalVaultExchange.UpdateArchive(source, initial, destination));
        Assert.Equal([42, 43], File.ReadAllBytes(destination));

        using var stream = File.OpenRead(initial);
        Assert.Equal("v1", Assert.Single(NoteArchive.ReadCollection(stream).Notes).Current.Markdown);
    }

    [Fact]
    public void UpdateCannotWriteNewArchiveIntoSourceVault()
    {
        var source = Vault();
        Put(source, "note.md", "body");
        var previous = Path.Combine(root, "previous.zip");
        LocalVaultExchange.CaptureArchive(source, previous);
        Assert.Throws<ArgumentException>(() =>
            LocalVaultExchange.UpdateArchive(source, previous,
                Path.Combine(source, "new.zip")));
        Assert.False(Path.Exists(Path.Combine(source, "new.zip")));
    }

    [Fact]
    public void UpdateCannotReadArchiveFromInsideSourceVault()
    {
        var source = Vault();
        Put(source, "note.md", "body");
        var previous = Path.Combine(root, "previous.zip");
        LocalVaultExchange.CaptureArchive(source, previous);
        var inside = Path.Combine(source, "old.zip");
        File.Copy(previous, inside);

        Assert.Throws<ArgumentException>(() =>
            LocalVaultExchange.PlanUpdate(source, inside));
    }

    [Fact]
    public void InvalidPreviousArchiveCannotPublishAnUpdate()
    {
        var source = Vault();
        Put(source, "note.md", "body");
        var baseline = Path.Combine(root, "bad.zip");
        File.WriteAllBytes(baseline, [1, 2, 3]);
        var next = Path.Combine(root, "no-output.zip");

        Assert.Throws<InvalidDataException>(() =>
            LocalVaultExchange.UpdateArchive(source, baseline, next));
        Assert.False(Path.Exists(next));
    }

    private string Vault()
    {
        var directory = Path.Combine(root, "vault");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Put(string directory, string relativePath, string text)
    {
        var target = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllBytes(target, Encoding.UTF8.GetBytes(text));
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
