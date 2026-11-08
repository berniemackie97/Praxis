using System.Text;
using Praxis.Knowledge;
using Xunit;

namespace Praxis.Knowledge.Tests;

public sealed class VaultTrackedReferencesTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "praxis-vault-links-" + Guid.NewGuid().ToString("N"));

    public VaultTrackedReferencesTests() => Directory.CreateDirectory(root);

    [Fact]
    public void VersionThreeRoundTripSeparatesExplicitAndVaultReferences()
    {
        var note = NoteRecord.Create("note.md", "![[image.png]]");
        var image = AttachmentRecord.Create("image.png", [1]);
        var reference = new AttachmentReference(note.Id, image.Id);
        var collection = new KnowledgeCollection([note], [image], [reference], [reference]);
        using var stream = new MemoryStream();

        NoteArchive.WriteCollection(stream, collection);
        stream.Position = 0;
        var result = NoteArchive.ReadCollection(stream);

        Assert.Equal(reference, Assert.Single(result.References));
        Assert.Equal(reference, Assert.Single(result.VaultReferences!));
        Assert.NotSame(result.References, result.VaultReferences);
    }

    [Fact]
    public void VersionTwoArchiveRemainsUntrackedAfterRoundTrip()
    {
        var collection = new KnowledgeCollection([NoteRecord.Create("note.md", "body")], [], []);
        using var stream = new MemoryStream();

        NoteArchive.WriteCollection(stream, collection);
        stream.Position = 0;
        var result = NoteArchive.ReadCollection(stream);

        Assert.Null(result.VaultReferences);
    }

    [Fact]
    public void InitialTrackedCaptureWritesNoSidecarAndKeepsSourceUntouched()
    {
        var source = Vault();
        Put(source, "note.md", "![[assets/image.png]]");
        Put(source, "assets/image.png", "binary");
        var baseline = Path.Combine(root, "first.zip");
        var before = File.ReadAllBytes(Path.Combine(source, "note.md"));

        LocalVaultExchange.CaptureArchive(source, baseline, trackLinks: true);
        var result = Read(baseline);

        Assert.Single(result.VaultReferences!);
        Assert.Empty(result.References);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(source, "note.md")));
        Assert.Equal(2, Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public void UnchangedRescanRetainsTrackedPairsAndHistory()
    {
        var source = Vault();
        Put(source, "note.md", "![[image.png]]");
        Put(source, "image.png", "binary");
        var previous = LocalVaultAttachmentLinks.Track(LocalVaultExchange.Capture(source));

        var result = LocalVaultReconciler.Reconcile(previous, LocalVaultExchange.Capture(source));

        Assert.Empty(result.Changes);
        Assert.Equal(0, result.AddedLinks);
        Assert.Equal(0, result.RemovedLinks);
        Assert.Equal(Assert.Single(previous.VaultReferences!), Assert.Single(result.Collection.VaultReferences!));
        Assert.Equal(previous.Notes[0].Id, result.Collection.Notes[0].Id);
        Assert.Single(result.Collection.Notes[0].Revisions);
    }

    [Fact]
    public void AddingAnEmbedCreatesATrackedReferenceWithoutOptingInAgain()
    {
        var source = Vault();
        Put(source, "note.md", "plain");
        Put(source, "a.png", "one");
        var previous = LocalVaultAttachmentLinks.Track(LocalVaultExchange.Capture(source));
        Put(source, "note.md", "![[a.png]]");

        var result = LocalVaultReconciler.Reconcile(previous, LocalVaultExchange.Capture(source));

        Assert.Equal(1, result.AddedLinks);
        Assert.Equal(0, result.RemovedLinks);
        Assert.Equal(previous.Attachments[0].Id,
            Assert.Single(result.Collection.VaultReferences!).AttachmentId);
    }

    [Fact]
    public void RemovingEmbedRequiresExplicitApprovalBeforePublishing()
    {
        var source = Vault();
        Put(source, "note.md", "![[image.png]]");
        Put(source, "image.png", "data");
        var firstPath = Path.Combine(root, "first.zip");
        LocalVaultExchange.CaptureArchive(source, firstPath, trackLinks: true);
        var before = File.ReadAllBytes(firstPath);
        Put(source, "note.md", "# Updated");
        var nextPath = Path.Combine(root, "next.zip");

        Assert.Throws<InvalidOperationException>(() =>
            LocalVaultExchange.UpdateArchive(source, firstPath, nextPath));
        Assert.False(File.Exists(nextPath));
        Assert.Equal(before, File.ReadAllBytes(firstPath));

        var plan = LocalVaultExchange.PlanUpdate(source, firstPath,
            acceptLinkRemovals: true);
        Assert.Equal(1, plan.RemovedLinks);
        Assert.Empty(plan.Collection.VaultReferences!);
        Assert.False(File.Exists(nextPath));

        LocalVaultExchange.UpdateArchive(source, firstPath, nextPath,
            acceptLinkRemovals: true);
        var updated = Read(nextPath);
        Assert.NotNull(updated.VaultReferences);
        Assert.Empty(updated.VaultReferences);
        Assert.Equal(2, Assert.Single(updated.Notes).Revisions.Count);
    }

    [Fact]
    public void ExplicitReferenceRemainsAfterApprovedRemovalOfSameTrackedPair()
    {
        var source = Vault();
        Put(source, "note.md", "![[image.png]]");
        Put(source, "image.png", "data");
        var captured = LocalVaultExchange.Capture(source);
        var reference = new AttachmentReference(captured.Notes[0].Id, captured.Attachments[0].Id);
        var previous = LocalVaultAttachmentLinks.Track(new KnowledgeCollection(
            captured.Notes, captured.Attachments, [reference]));
        Put(source, "note.md", "no embed");

        var result = LocalVaultReconciler.Reconcile(previous, LocalVaultExchange.Capture(source),
            acceptLinkRemovals: true);

        Assert.Empty(result.Collection.VaultReferences!);
        Assert.Equal(reference, Assert.Single(result.Collection.References));
        Assert.Equal(1, result.RemovedLinks);
    }

    [Fact]
    public void RenamingAttachmentAndUpdatingMarkdownPreservesTrackedIdentity()
    {
        var source = Vault();
        Put(source, "note.md", "![[old.png]]");
        Put(source, "old.png", "data");
        var previous = LocalVaultAttachmentLinks.Track(LocalVaultExchange.Capture(source));
        var priorId = Assert.Single(previous.Attachments).Id;
        File.Move(Path.Combine(source, "old.png"), Path.Combine(source, "new.png"));
        Put(source, "note.md", "![[new.png]]");

        var result = LocalVaultReconciler.Reconcile(previous, LocalVaultExchange.Capture(source),
            [new VaultRename("old.png", "new.png")]);

        Assert.Equal(priorId, Assert.Single(result.Collection.Attachments).Id);
        Assert.Equal(priorId, Assert.Single(result.Collection.VaultReferences!).AttachmentId);
        Assert.Equal(0, result.AddedLinks);
        Assert.Equal(0, result.RemovedLinks);
    }

    [Fact]
    public void RenamedAttachmentWithStaleMarkdownCannotPublish()
    {
        var source = Vault();
        Put(source, "note.md", "![[old.png]]");
        Put(source, "old.png", "data");
        var first = Path.Combine(root, "first.zip");
        LocalVaultExchange.CaptureArchive(source, first, trackLinks: true);
        File.Move(Path.Combine(source, "old.png"), Path.Combine(source, "new.png"));
        var destination = Path.Combine(root, "second.zip");

        var error = Assert.Throws<InvalidDataException>(() =>
            LocalVaultExchange.UpdateArchive(source, first, destination,
                [new VaultRename("old.png", "new.png")]));
        Assert.Contains("old.png", error.Message);
        Assert.False(File.Exists(destination));
        Assert.True(File.Exists(first));
    }

    [Fact]
    public void AmbiguousTrackedCaptureDoesNotPublishArchive()
    {
        var source = Vault();
        Put(source, "note.md", "![[image.png]]");
        Put(source, "a/image.png", "one");
        Put(source, "b/image.png", "two");
        var output = Path.Combine(root, "ambiguous.zip");

        var error = Assert.Throws<InvalidDataException>(() =>
            LocalVaultExchange.CaptureArchive(source, output, trackLinks: true));

        Assert.Contains("Multiple attachments match", error.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void VersionTwoBaselineCanOptInWithoutLosingHistoryOrExplicitReferences()
    {
        var source = Vault();
        Put(source, "note.md", "![[image.png]]");
        Put(source, "image.png", "data");
        var first = Path.Combine(root, "legacy.zip");
        LocalVaultExchange.CaptureArchive(source, first);
        var baseline = Read(first);
        Assert.Null(baseline.VaultReferences);
        var reference = new AttachmentReference(baseline.Notes[0].Id, baseline.Attachments[0].Id);
        var withExplicit = Path.Combine(root, "explicit.zip");
        using (var output = File.Create(withExplicit))
        {
            NoteArchive.WriteCollection(output, new KnowledgeCollection(
                baseline.Notes, baseline.Attachments, [reference]));
        }

        Put(source, "note.md", "![[image.png]]\nsecond line");
        var next = Path.Combine(root, "tracked.zip");
        LocalVaultExchange.UpdateArchive(source, withExplicit, next, trackLinks: true);
        var result = Read(next);

        Assert.Equal(reference, Assert.Single(result.References));
        Assert.Equal(reference, Assert.Single(result.VaultReferences!));
        Assert.Equal(baseline.Notes[0].Id, result.Notes[0].Id);
        Assert.Equal(2, Assert.Single(result.Notes).Revisions.Count);
        Assert.Equal(baseline.Attachments[0].Id, result.Attachments[0].Id);
    }

    [Fact]
    public void InvalidVaultReferencesAreRejectedBeforeArchivePublication()
    {
        var note = NoteRecord.Create("note.md", "body");
        var other = AttachmentRecord.Create("other.png", [1]);
        var invalid = new AttachmentReference(note.Id, Guid.NewGuid());
        Assert.Throws<ArgumentException>(() =>
            new KnowledgeCollection([note], [other], [], [invalid]));
        Assert.Throws<ArgumentException>(() =>
            new KnowledgeCollection([note], [other], [],
                [new AttachmentReference(note.Id, other.Id),
                 new AttachmentReference(note.Id, other.Id)]));
    }

    private string Vault()
    {
        var path = Path.Combine(root, "vault");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Put(string directory, string relative, string content)
    {
        var path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
    }

    private static KnowledgeCollection Read(string path)
    {
        using var input = File.OpenRead(path);
        return NoteArchive.ReadCollection(input);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
