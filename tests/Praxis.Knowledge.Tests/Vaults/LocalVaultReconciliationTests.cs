using System.Text;
using System.Text.Json.Nodes;
using Praxis.Knowledge.Archives;
using Praxis.Knowledge.Model;
using Praxis.Knowledge.Tests.Archives;
using Praxis.Knowledge.Vaults;
using Xunit;

namespace Praxis.Knowledge.Tests.Vaults;

public sealed class LocalVaultReconciliationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "praxis-vault-update-" + Guid.NewGuid().ToString("N"));

    public LocalVaultReconciliationTests() => Directory.CreateDirectory(root);

    [Fact]
    public void UnchangedRescanRetainsIdentityAndDoesNotCreateRevisions()
    {
        string source = Vault();
        Put(source, "Notes/first.md", "# First\r\n");
        Put(source, "media/file.bin", "\0\ud7ff");
        KnowledgeCollection previous = LocalVaultExchange.Capture(source);

        VaultUpdate updated = LocalVaultReconciler.Reconcile(previous, LocalVaultExchange.Capture(source));

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
        string source = Vault();
        Put(source, "Notes/first.md", "# Before\n");
        KnowledgeCollection previous = LocalVaultExchange.Capture(source);
        Put(source, "Notes/first.md", "# After\r\n");

        VaultUpdate result = LocalVaultReconciler.Reconcile(previous, LocalVaultExchange.Capture(source));
        NoteRecord note = Assert.Single(result.Collection.Notes);
        Assert.Equal(Assert.Single(previous.Notes).Id, note.Id);
        Assert.Equal(2, note.Revisions.Count);
        Assert.Equal("# Before\n", note.Revisions[0].Markdown);
        Assert.Equal("# After\r\n", note.Revisions[1].Markdown);
        Assert.Equal(note.Id, Assert.Single(result.Changes).RecordId);

        VaultUpdate unchanged = LocalVaultReconciler.Reconcile(result.Collection, LocalVaultExchange.Capture(source));
        Assert.Empty(unchanged.Changes);
        Assert.Equal(2, Assert.Single(unchanged.Collection.Notes).Revisions.Count);
    }

    [Fact]
    public void ChangingAttachmentBytesKeepsIdentityAndPreviousExplicitReferences()
    {
        string source = Vault();
        Put(source, "notes.md", "# Linked\n");
        Put(source, "media/photo.png", "\0\x01\x02");
        KnowledgeCollection captured = LocalVaultExchange.Capture(source);
        NoteRecord note = Assert.Single(captured.Notes);
        AttachmentRecord attachment = Assert.Single(captured.Attachments);
        KnowledgeCollection previous = new(captured.Notes, captured.Attachments,
            [new AttachmentReference(note.Id, attachment.Id)]);
        Put(source, "media/photo.png", "\0\x01\x03");

        VaultUpdate result = LocalVaultReconciler.Reconcile(previous, LocalVaultExchange.Capture(source));
        AttachmentRecord updated = Assert.Single(result.Collection.Attachments);
        Assert.Equal(attachment.Id, updated.Id);
        Assert.NotEqual(attachment.Sha256, updated.Sha256);
        Assert.Equal([0, 1, 3], updated.ReadContent());
        Assert.Equal(new AttachmentReference(note.Id, attachment.Id),
            Assert.Single(result.Collection.References));
        Assert.True(Assert.Single(result.Changes).ContentChanged);
        Assert.False(Assert.Single(result.Changes).PathChanged);
    }

    [Fact]
    public void ExplicitFolderRenameRetainsNoteHistoryAttachmentIdentityAndTrackedLinks()
    {
        string source = Vault();
        Put(source, "Old/note.md", "![](media/photo.png)");
        Put(source, "Old/media/photo.png", "\0\x03");
        Put(source, "Old/nested/other.md", "# Other");
        string previousPath = Path.Combine(root, "folder-original.zip");
        LocalVaultExchange.CaptureArchive(source, previousPath, trackLinks: true);
        using FileStream previousStream = File.OpenRead(previousPath);
        KnowledgeCollection previous = NoteArchive.ReadCollection(previousStream);

        Directory.Move(Path.Combine(source, "Old"), Path.Combine(source, "New"));
        VaultRename[] renames = [new VaultRename("Old", "New", IsFolder: true)];

        VaultUpdate preview = LocalVaultExchange.PlanUpdate(source, previousPath, renames);
        Assert.Equal(3, preview.Changes.Count);
        Assert.All(preview.Changes, change => Assert.True(change.PathChanged));
        Assert.Equal(0, preview.RemovedLinks);

        string newPath = Path.Combine(root, "folder-updated.zip");
        VaultUpdate result = LocalVaultExchange.UpdateArchive(source, previousPath, newPath, renames);
        Assert.Equal(previous.VaultReferences, result.Collection.VaultReferences);
        foreach (NoteRecord original in previous.Notes)
        {
            NoteRecord updated = Assert.Single(result.Collection.Notes,
                note => note.Id == original.Id);
            Assert.Equal("New" + original.Current.Path["Old".Length..], updated.Current.Path);
            Assert.Equal(2, updated.Revisions.Count);
            Assert.Equal(original.Current.Markdown, updated.Current.Markdown);
        }

        Assert.Equal(previous.Attachments[0].Id, Assert.Single(result.Collection.Attachments).Id);
        Assert.Equal("New/media/photo.png", result.Collection.Attachments[0].Path);

        string restored = Path.Combine(root, "folder-restored");
        LocalVaultExchange.RestoreArchive(newPath, restored);
        Assert.Equal("![](media/photo.png)",
            File.ReadAllText(Path.Combine(restored, "New/note.md")));
        Assert.Equal([0, 3], File.ReadAllBytes(Path.Combine(restored, "New/media/photo.png")));
        Assert.True(File.Exists(previousPath));
    }

    [Fact]
    public void OrderedFolderAndFileMovesResolveFromOriginalIdentity()
    {
        KnowledgeCollection previous = new(
            [NoteRecord.Create("Old/notes/note.md", "note")],
            [AttachmentRecord.Create("Old/media/image.bin", [1, 2, 3])], []);
        KnowledgeCollection captured = new(
            [NoteRecord.Create("Final/notes/renamed.md", "note")],
            [AttachmentRecord.Create("Final/media/image.bin", [1, 2, 3])], []);
        VaultRename[] rules =
        [
            new VaultRename("Old", "Middle", IsFolder: true),
            new VaultRename("Middle", "Final", IsFolder: true),
            new VaultRename("Final/notes/note.md", "Final/notes/renamed.md")
        ];

        VaultUpdate updated = LocalVaultReconciler.Reconcile(previous, captured, rules);
        Assert.Equal(previous.Notes[0].Id, updated.Collection.Notes[0].Id);
        Assert.Equal(previous.Attachments[0].Id, updated.Collection.Attachments[0].Id);
        Assert.Equal("Final/notes/renamed.md", updated.Collection.Notes[0].Current.Path);
        Assert.Equal("Final/media/image.bin", updated.Collection.Attachments[0].Path);
    }

    [Fact]
    public void InvalidFolderMappingsCannotInferOrSilentlyOverwriteIdentities()
    {
        KnowledgeCollection previous = new([
            NoteRecord.Create("Old/a.md", "A"), NoteRecord.Create("Elsewhere/b.md", "B")
        ], [], []);
        KnowledgeCollection observed = new([
            NoteRecord.Create("New/a.md", "A"), NoteRecord.Create("Elsewhere/b.md", "B")
        ], [], []);

        Assert.Throws<InvalidDataException>(() => LocalVaultReconciler.Reconcile(
            previous, observed, [new VaultRename("Missing", "New", IsFolder: true)]));
        Assert.Throws<InvalidDataException>(() => LocalVaultReconciler.Reconcile(
            previous, observed, [new VaultRename("Old", "Nowhere", IsFolder: true)]));
        Assert.Throws<InvalidDataException>(() => LocalVaultReconciler.Reconcile(
            previous, observed, [
                new VaultRename("Old", "New", IsFolder: true),
                new VaultRename("Old/a.md", "New/a.md")
            ]));
        Assert.Throws<ArgumentException>(() => LocalVaultReconciler.Reconcile(
            previous, observed, [new VaultRename("Old", "../escape", IsFolder: true)]));
        Assert.Throws<InvalidDataException>(() => LocalVaultReconciler.Reconcile(
            previous, observed, Enumerable.Repeat(
                new VaultRename("Old", "New", IsFolder: true), 2_001).ToArray()));
    }

    [Fact]
    public void FolderRenameOnlyMatchesWholePathSegments()
    {
        KnowledgeCollection previous = new([
            NoteRecord.Create("Old/a.md", "a"),
            NoteRecord.Create("Older/b.md", "b")
        ], [], []);
        KnowledgeCollection observed = new([
            NoteRecord.Create("New/a.md", "a"),
            NoteRecord.Create("Older/b.md", "b")
        ], [], []);

        VaultUpdate updated = LocalVaultReconciler.Reconcile(previous, observed,
            [new VaultRename("Old", "New", IsFolder: true)]);
        Assert.Equal(previous.Notes[0].Id,
            Assert.Single(updated.Collection.Notes, note => note.Current.Path == "New/a.md").Id);
        Assert.Equal(previous.Notes[1].Id,
            Assert.Single(updated.Collection.Notes, note => note.Current.Path == "Older/b.md").Id);
    }

    [Fact]
    public void ExplicitNoteRenameAndEditPreserveIdentityAndRevisionHistory()
    {
        string source = Vault();
        Put(source, "Old/first.md", "# Before");
        KnowledgeCollection previous = LocalVaultExchange.Capture(source);
        File.Delete(Path.Combine(source, "Old/first.md"));
        Directory.Delete(Path.Combine(source, "Old"));
        Put(source, "New/moved.md", "# After");
        VaultRename[] mapping = [new VaultRename("Old/first.md", "New/moved.md")];

        VaultUpdate result = LocalVaultReconciler.Reconcile(previous, LocalVaultExchange.Capture(source),
            mapping);
        NoteRecord updated = Assert.Single(result.Collection.Notes);
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
        string source = Vault();
        Put(source, "note.md", "![[media/old.png]]");
        Put(source, "media/old.png", "\x01\x02\x03");
        KnowledgeCollection first = LocalVaultExchange.Capture(source);
        KnowledgeCollection previous = new(first.Notes, first.Attachments,
            [new AttachmentReference(first.Notes[0].Id, first.Attachments[0].Id)]);
        File.Move(Path.Combine(source, "media/old.png"), Path.Combine(source, "media/new.png"));

        VaultUpdate updated = LocalVaultReconciler.Reconcile(previous, LocalVaultExchange.Capture(source),
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
        KnowledgeCollection before = new([
            NoteRecord.Create("one.md", "one"),
            NoteRecord.Create("two.md", "two")], [], []);
        KnowledgeCollection observed = new([
            NoteRecord.Create("one.md", "two"),
            NoteRecord.Create("two.md", "one")], [], []);

        VaultUpdate result = LocalVaultReconciler.Reconcile(before, observed, [
            new VaultRename("one.md", "two.md"),
            new VaultRename("two.md", "one.md")]);

        NoteRecord one = Assert.Single(result.Collection.Notes, note => note.Current.Path == "two.md");
        NoteRecord two = Assert.Single(result.Collection.Notes, note => note.Current.Path == "one.md");
        Assert.Equal(before.Notes[0].Id, one.Id);
        Assert.Equal(before.Notes[1].Id, two.Id);
        Assert.Equal(2, one.Current.Number);
        Assert.Equal(2, two.Current.Number);
    }

    [Fact]
    public void CaseOnlyNoteRenamePreservesIdentityAndAppendsRevision()
    {
        KnowledgeCollection before = new([NoteRecord.Create("Draft.md", "body")], [], []);
        KnowledgeCollection observed = new([NoteRecord.Create("draft.md", "body")], [], []);

        VaultUpdate result = LocalVaultReconciler.Reconcile(before, observed);

        Assert.Equal(before.Notes[0].Id, Assert.Single(result.Collection.Notes).Id);
        Assert.Equal(2, result.Collection.Notes[0].Current.Number);
        Assert.True(Assert.Single(result.Changes).PathChanged);
        Assert.False(Assert.Single(result.Changes).ContentChanged);
    }

    [Fact]
    public void NoteAndAttachmentWithEqualUuidsRemainDistinctDuringRenames()
    {
        NoteRecord note = NoteRecord.Create("old.md", "body");
        AttachmentRecord attachment = AttachmentRecord.Create("old.bin", [1, 2]);
        using MemoryStream archive = new();
        NoteArchive.WriteCollection(archive,
            new KnowledgeCollection([note], [attachment], []));
        archive.Position = 0;
        ArchiveFixture.ReplaceEntry(archive, "manifest.json", bytes =>
        {
            JsonObject manifest = JsonNode.Parse(Encoding.UTF8.GetString(bytes))!.AsObject();
            manifest["attachments"]!.AsArray()[0]!["id"] = note.Id;
            return Encoding.UTF8.GetBytes(manifest.ToJsonString());
        });
        archive.Position = 0;
        KnowledgeCollection previous = NoteArchive.ReadCollection(archive);
        Assert.Equal(previous.Notes[0].Id, previous.Attachments[0].Id);

        KnowledgeCollection observed = new(
            [NoteRecord.Create("new.md", "body")],
            [AttachmentRecord.Create("new.bin", [1, 2])], []);
        VaultUpdate updated = LocalVaultReconciler.Reconcile(previous, observed, [
            new VaultRename("old.md", "new.md"),
            new VaultRename("old.bin", "new.bin")
        ]);

        Assert.Equal(note.Id, Assert.Single(updated.Collection.Notes).Id);
        Assert.Equal(note.Id, Assert.Single(updated.Collection.Attachments).Id);
        Assert.Equal(2, updated.Changes.Count);
        Assert.All(updated.Changes, change => Assert.True(change.PathChanged));
    }

    [Fact]
    public void AddingFilesRetainsAllExistingIdentitiesAndReferences()
    {
        string source = Vault();
        Put(source, "base.md", "before");
        KnowledgeCollection previous = LocalVaultExchange.Capture(source);
        Put(source, "added.md", "new");
        Put(source, "photo.jpg", "\0\x06");

        VaultUpdate result = LocalVaultReconciler.Reconcile(previous, LocalVaultExchange.Capture(source));
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
        string source = Vault();
        Put(source, "original.md", "body");
        string original = Path.Combine(root, "original.zip");
        LocalVaultExchange.CaptureArchive(source, original);
        byte[] snapshot = File.ReadAllBytes(original);
        File.Delete(Path.Combine(source, "original.md"));
        Put(source, "new.md", "body");
        string next = Path.Combine(root, "next.zip");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            LocalVaultExchange.UpdateArchive(source, original, next));
        Assert.Contains("original.md", error.Message);
        Assert.False(Path.Exists(next));
        Assert.Equal(snapshot, File.ReadAllBytes(original));
    }

    [Fact]
    public void ChangingFileTypeRequiresExplicitReview()
    {
        KnowledgeCollection before = new([NoteRecord.Create("file.md", "# A")], [], []);
        KnowledgeCollection observed = new([], [AttachmentRecord.Create("file.md", [1])], []);

        Assert.Throws<InvalidDataException>(() =>
            LocalVaultReconciler.Reconcile(before, observed));
    }

    [Fact]
    public void IdenticalBytePayloadDoesNotAutomaticallyImplyRename()
    {
        KnowledgeCollection before = new([], [AttachmentRecord.Create("old.png", [1, 2])], []);
        KnowledgeCollection observed = new([], [AttachmentRecord.Create("new.png", [1, 2])], []);

        Assert.Throws<InvalidOperationException>(() =>
            LocalVaultReconciler.Reconcile(before, observed));
    }

    [Fact]
    public void UnknownSourceOrMissingTargetMappingFails()
    {
        KnowledgeCollection before = new([NoteRecord.Create("old.md", "old")], [], []);
        KnowledgeCollection observed = new([NoteRecord.Create("new.md", "new")], [], []);

        Assert.Throws<InvalidDataException>(() => LocalVaultReconciler.Reconcile(before,
            observed, [new VaultRename("unknown.md", "new.md")]));
        Assert.Throws<InvalidDataException>(() => LocalVaultReconciler.Reconcile(before,
            observed, [new VaultRename("old.md", "absent.md")]));
    }

    [Fact]
    public void DuplicateOrConflictingRenamesFailWithoutUsingWrongIdentity()
    {
        KnowledgeCollection before = new([
            NoteRecord.Create("one.md", "one"),
            NoteRecord.Create("two.md", "two")], [], []);
        KnowledgeCollection observed = new([
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
        KnowledgeCollection before = new([NoteRecord.Create("old.md", "old")], [], []);
        KnowledgeCollection observed = new([NoteRecord.Create("new.md", "new")], [], []);

        Assert.Throws<ArgumentException>(() => LocalVaultReconciler.Reconcile(before,
            observed, [new VaultRename("old.md", "../escape.md")]));
    }

    [Fact]
    public void ReconcilerRejectsArbitraryObservedReferencesAndHistories()
    {
        NoteRecord note = NoteRecord.Create("note.md", "first");
        AttachmentRecord attachment = AttachmentRecord.Create("photo.png", [1]);
        KnowledgeCollection before = new([note], [attachment],
            [new AttachmentReference(note.Id, attachment.Id)]);
        KnowledgeCollection captured = new([note.Revise("note.md", "second")], [], []);

        Assert.Throws<ArgumentException>(() =>
            LocalVaultReconciler.Reconcile(before, captured));
        Assert.Throws<ArgumentException>(() =>
            LocalVaultReconciler.Reconcile(before, before));
    }

    [Fact]
    public void UpdateArchivePreservesPreviousHistoryAndRestoresTheLatestFiles()
    {
        string source = Vault();
        Put(source, "note.md", "v1\n");
        Put(source, "original.bin", "\0\x03");
        string firstPath = Path.Combine(root, "first.zip");
        LocalVaultExchange.CaptureArchive(source, firstPath);
        using FileStream beforeStream = File.OpenRead(firstPath);
        KnowledgeCollection before = NoteArchive.ReadCollection(beforeStream);

        Put(source, "note.md", "v2\r\n");
        Put(source, "original.bin", "\0\x04");
        string secondPath = Path.Combine(root, "second.zip");
        VaultUpdate update = LocalVaultExchange.UpdateArchive(source, firstPath, secondPath);
        Assert.Equal(2, update.Changes.Count);

        using FileStream stream = File.OpenRead(secondPath);
        KnowledgeCollection second = NoteArchive.ReadCollection(stream);
        Assert.Equal(before.Notes[0].Id, second.Notes[0].Id);
        Assert.Equal(2, second.Notes[0].Revisions.Count);
        Assert.Equal(before.Attachments[0].Id, second.Attachments[0].Id);
        Assert.NotEqual(before.Attachments[0].Sha256, second.Attachments[0].Sha256);

        string restored = Path.Combine(root, "restored");
        LocalVaultExchange.RestoreArchive(secondPath, restored);
        Assert.Equal("v2\r\n", File.ReadAllText(Path.Combine(restored, "note.md")));
        Assert.Equal([0, 4], File.ReadAllBytes(Path.Combine(restored, "original.bin")));
    }

    [Fact]
    public void ExistingTargetIsNotOverwrittenAndBaselineRemainsValid()
    {
        string source = Vault();
        Put(source, "note.md", "v1");
        string initial = Path.Combine(root, "initial.zip");
        LocalVaultExchange.CaptureArchive(source, initial);
        Put(source, "note.md", "v2");

        string destination = Path.Combine(root, "existing.zip");
        File.WriteAllBytes(destination, [42, 43]);
        Assert.Throws<IOException>(() =>
            LocalVaultExchange.UpdateArchive(source, initial, destination));
        Assert.Equal([42, 43], File.ReadAllBytes(destination));

        using FileStream stream = File.OpenRead(initial);
        Assert.Equal("v1", Assert.Single(NoteArchive.ReadCollection(stream).Notes).Current.Markdown);
    }

    [Fact]
    public void UpdateCannotWriteNewArchiveIntoSourceVault()
    {
        string source = Vault();
        Put(source, "note.md", "body");
        string previous = Path.Combine(root, "previous.zip");
        LocalVaultExchange.CaptureArchive(source, previous);
        Assert.Throws<ArgumentException>(() =>
            LocalVaultExchange.UpdateArchive(source, previous,
                Path.Combine(source, "new.zip")));
        Assert.False(Path.Exists(Path.Combine(source, "new.zip")));
    }

    [Fact]
    public void UpdateCannotReadArchiveFromInsideSourceVault()
    {
        string source = Vault();
        Put(source, "note.md", "body");
        string previous = Path.Combine(root, "previous.zip");
        LocalVaultExchange.CaptureArchive(source, previous);
        string inside = Path.Combine(source, "old.zip");
        File.Copy(previous, inside);

        Assert.Throws<ArgumentException>(() =>
            LocalVaultExchange.PlanUpdate(source, inside));
    }

    [Fact]
    public void InvalidPreviousArchiveCannotPublishAnUpdate()
    {
        string source = Vault();
        Put(source, "note.md", "body");
        string baseline = Path.Combine(root, "bad.zip");
        File.WriteAllBytes(baseline, [1, 2, 3]);
        string next = Path.Combine(root, "no-output.zip");

        Assert.Throws<InvalidDataException>(() =>
            LocalVaultExchange.UpdateArchive(source, baseline, next));
        Assert.False(Path.Exists(next));
    }

    private string Vault()
    {
        string directory = Path.Combine(root, "vault");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void Put(string directory, string relativePath, string text)
    {
        string target = Path.Combine(directory, relativePath.Replace('/', Path.DirectorySeparatorChar));
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
