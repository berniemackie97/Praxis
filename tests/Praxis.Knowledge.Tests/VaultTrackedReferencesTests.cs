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
        NoteRecord note = NoteRecord.Create("note.md", "![[image.png]]");
        AttachmentRecord image = AttachmentRecord.Create("image.png", [1]);
        AttachmentReference reference = new AttachmentReference(note.Id, image.Id);
        KnowledgeCollection collection = new KnowledgeCollection([note], [image], [reference], [reference]);
        using MemoryStream stream = new MemoryStream();

        NoteArchive.WriteCollection(stream, collection);
        stream.Position = 0;
        KnowledgeCollection result = NoteArchive.ReadCollection(stream);

        Assert.Equal(reference, Assert.Single(result.References));
        Assert.Equal(reference, Assert.Single(result.VaultReferences!));
        Assert.NotSame(result.References, result.VaultReferences);
    }

    [Fact]
    public void VersionTwoArchiveRemainsUntrackedAfterRoundTrip()
    {
        KnowledgeCollection collection = new KnowledgeCollection([NoteRecord.Create("note.md", "body")], [], []);
        using MemoryStream stream = new MemoryStream();

        NoteArchive.WriteCollection(stream, collection);
        stream.Position = 0;
        KnowledgeCollection result = NoteArchive.ReadCollection(stream);

        Assert.Null(result.VaultReferences);
    }

    [Fact]
    public void InitialTrackedCaptureWritesNoSidecarAndKeepsSourceUntouched()
    {
        string source = Vault();
        Put(source, "note.md", "![[assets/image.png]]");
        Put(source, "assets/image.png", "binary");
        string baseline = Path.Combine(root, "first.zip");
        byte[] before = File.ReadAllBytes(Path.Combine(source, "note.md"));

        LocalVaultExchange.CaptureArchive(source, baseline, trackLinks: true);
        KnowledgeCollection result = Read(baseline);

        Assert.Single(result.VaultReferences!);
        Assert.Empty(result.References);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(source, "note.md")));
        Assert.Equal(2, Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public void UnchangedRescanRetainsTrackedPairsAndHistory()
    {
        string source = Vault();
        Put(source, "note.md", "![[image.png]]");
        Put(source, "image.png", "binary");
        KnowledgeCollection previous = LocalVaultAttachmentLinks.Track(LocalVaultExchange.Capture(source));

        VaultUpdate result = LocalVaultReconciler.Reconcile(previous, LocalVaultExchange.Capture(source));

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
        string source = Vault();
        Put(source, "note.md", "plain");
        Put(source, "a.png", "one");
        KnowledgeCollection previous = LocalVaultAttachmentLinks.Track(LocalVaultExchange.Capture(source));
        Put(source, "note.md", "![[a.png]]");

        VaultUpdate result = LocalVaultReconciler.Reconcile(previous, LocalVaultExchange.Capture(source));

        Assert.Equal(1, result.AddedLinks);
        Assert.Equal(0, result.RemovedLinks);
        Assert.Equal(previous.Attachments[0].Id,
            Assert.Single(result.Collection.VaultReferences!).AttachmentId);
    }

    [Fact]
    public void RemovingEmbedRequiresExplicitApprovalBeforePublishing()
    {
        string source = Vault();
        Put(source, "note.md", "![[image.png]]");
        Put(source, "image.png", "data");
        string firstPath = Path.Combine(root, "first.zip");
        LocalVaultExchange.CaptureArchive(source, firstPath, trackLinks: true);
        byte[] before = File.ReadAllBytes(firstPath);
        Put(source, "note.md", "# Updated");
        string nextPath = Path.Combine(root, "next.zip");

        Assert.Throws<InvalidOperationException>(() =>
            LocalVaultExchange.UpdateArchive(source, firstPath, nextPath));
        Assert.False(File.Exists(nextPath));
        Assert.Equal(before, File.ReadAllBytes(firstPath));

        VaultUpdate plan = LocalVaultExchange.PlanUpdate(source, firstPath);
        Assert.Equal(1, plan.RemovedLinks);
        Assert.Empty(plan.Collection.VaultReferences!);
        Assert.False(File.Exists(nextPath));

        LocalVaultExchange.UpdateArchive(source, firstPath, nextPath,
            approvedLinkRemovals: plan.RemovedLinkPairs);
        KnowledgeCollection updated = Read(nextPath);
        Assert.NotNull(updated.VaultReferences);
        Assert.Empty(updated.VaultReferences);
        Assert.Equal(2, Assert.Single(updated.Notes).Revisions.Count);
    }

    [Fact]
    public void ExplicitReferenceRemainsAfterApprovedRemovalOfSameTrackedPair()
    {
        string source = Vault();
        Put(source, "note.md", "![[image.png]]");
        Put(source, "image.png", "data");
        KnowledgeCollection captured = LocalVaultExchange.Capture(source);
        AttachmentReference reference = new AttachmentReference(captured.Notes[0].Id, captured.Attachments[0].Id);
        KnowledgeCollection previous = LocalVaultAttachmentLinks.Track(new KnowledgeCollection(
            captured.Notes, captured.Attachments, [reference]));
        Put(source, "note.md", "no embed");

        VaultUpdate result = LocalVaultReconciler.Reconcile(previous, LocalVaultExchange.Capture(source),
            previewLinkRemovals: true);

        Assert.Empty(result.Collection.VaultReferences!);
        Assert.Equal(reference, Assert.Single(result.Collection.References));
        Assert.Equal(1, result.RemovedLinks);
    }

    [Fact]
    public void ApprovalMustExactlyMatchRemovedPairIdentifiers()
    {
        string source = Vault();
        Put(source, "note.md", "![[first.png]]\n![[second.png]]");
        Put(source, "first.png", "one");
        Put(source, "second.png", "two");
        string first = Path.Combine(root, "baseline.zip");
        LocalVaultExchange.CaptureArchive(source, first, trackLinks: true);
        Put(source, "note.md", "no embeds");
        VaultUpdate plan = LocalVaultExchange.PlanUpdate(source, first);
        Assert.Equal(2, plan.RemovedLinks);

        string output = Path.Combine(root, "incorrect.zip");
        Assert.Throws<InvalidOperationException>(() =>
            LocalVaultExchange.UpdateArchive(source, first, output,
                approvedLinkRemovals: [plan.RemovedLinkPairs![0]]));
        Assert.False(File.Exists(output));

        Assert.Throws<InvalidOperationException>(() =>
            LocalVaultExchange.UpdateArchive(source, first, output,
                approvedLinkRemovals: [plan.RemovedLinkPairs![0], plan.RemovedLinkPairs[0]]));
        Assert.False(File.Exists(output));

        AttachmentReference alien = new AttachmentReference(Guid.NewGuid(), Guid.NewGuid());
        Assert.Throws<InvalidOperationException>(() =>
            LocalVaultExchange.UpdateArchive(source, first, output,
                approvedLinkRemovals: [plan.RemovedLinkPairs![0], alien]));
        Assert.False(File.Exists(output));

        LocalVaultExchange.UpdateArchive(source, first, output,
            approvedLinkRemovals: plan.RemovedLinkPairs!.Reverse().ToArray());
        Assert.Empty(Read(output).VaultReferences!);
    }

    [Fact]
    public void StaleApprovalCannotRemoveNewlyDisappearedReferences()
    {
        string source = Vault();
        Put(source, "note.md", "![[first.png]]\n![[second.png]]");
        Put(source, "first.png", "one");
        Put(source, "second.png", "two");
        string first = Path.Combine(root, "baseline.zip");
        LocalVaultExchange.CaptureArchive(source, first, trackLinks: true);

        Put(source, "note.md", "![[second.png]]");
        VaultUpdate plan = LocalVaultExchange.PlanUpdate(source, first);
        Assert.Single(plan.RemovedLinkPairs!);
        Put(source, "note.md", "no embeds");
        string output = Path.Combine(root, "changed-after-plan.zip");

        Assert.Throws<InvalidOperationException>(() =>
            LocalVaultExchange.UpdateArchive(source, first, output,
                approvedLinkRemovals: plan.RemovedLinkPairs));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void RenamingAttachmentAndUpdatingMarkdownPreservesTrackedIdentity()
    {
        string source = Vault();
        Put(source, "note.md", "![[old.png]]");
        Put(source, "old.png", "data");
        KnowledgeCollection previous = LocalVaultAttachmentLinks.Track(LocalVaultExchange.Capture(source));
        Guid priorId = Assert.Single(previous.Attachments).Id;
        File.Move(Path.Combine(source, "old.png"), Path.Combine(source, "new.png"));
        Put(source, "note.md", "![[new.png]]");

        VaultUpdate result = LocalVaultReconciler.Reconcile(previous, LocalVaultExchange.Capture(source),
            [new VaultRename("old.png", "new.png")]);

        Assert.Equal(priorId, Assert.Single(result.Collection.Attachments).Id);
        Assert.Equal(priorId, Assert.Single(result.Collection.VaultReferences!).AttachmentId);
        Assert.Equal(0, result.AddedLinks);
        Assert.Equal(0, result.RemovedLinks);
    }

    [Fact]
    public void RenamedAttachmentWithStaleMarkdownCannotPublish()
    {
        string source = Vault();
        Put(source, "note.md", "![[old.png]]");
        Put(source, "old.png", "data");
        string first = Path.Combine(root, "first.zip");
        LocalVaultExchange.CaptureArchive(source, first, trackLinks: true);
        File.Move(Path.Combine(source, "old.png"), Path.Combine(source, "new.png"));
        string destination = Path.Combine(root, "second.zip");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            LocalVaultExchange.UpdateArchive(source, first, destination,
                [new VaultRename("old.png", "new.png")]));
        Assert.Contains("old.png", error.Message);
        Assert.False(File.Exists(destination));
        Assert.True(File.Exists(first));
    }

    [Fact]
    public void AmbiguousTrackedCaptureDoesNotPublishArchive()
    {
        string source = Vault();
        Put(source, "note.md", "![[image.png]]");
        Put(source, "a/image.png", "one");
        Put(source, "b/image.png", "two");
        string output = Path.Combine(root, "ambiguous.zip");

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            LocalVaultExchange.CaptureArchive(source, output, trackLinks: true));

        Assert.Contains("Multiple attachments match", error.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void VersionTwoBaselineCanOptInWithoutLosingHistoryOrExplicitReferences()
    {
        string source = Vault();
        Put(source, "note.md", "![[image.png]]");
        Put(source, "image.png", "data");
        string first = Path.Combine(root, "legacy.zip");
        LocalVaultExchange.CaptureArchive(source, first);
        KnowledgeCollection baseline = Read(first);
        Assert.Null(baseline.VaultReferences);
        AttachmentReference reference = new AttachmentReference(baseline.Notes[0].Id, baseline.Attachments[0].Id);
        string withExplicit = Path.Combine(root, "explicit.zip");
        using (FileStream output = File.Create(withExplicit))
        {
            NoteArchive.WriteCollection(output, new KnowledgeCollection(
                baseline.Notes, baseline.Attachments, [reference]));
        }

        Put(source, "note.md", "![[image.png]]\nsecond line");
        string next = Path.Combine(root, "tracked.zip");
        LocalVaultExchange.UpdateArchive(source, withExplicit, next, trackLinks: true);
        KnowledgeCollection result = Read(next);

        Assert.Equal(reference, Assert.Single(result.References));
        Assert.Equal(reference, Assert.Single(result.VaultReferences!));
        Assert.Equal(baseline.Notes[0].Id, result.Notes[0].Id);
        Assert.Equal(2, Assert.Single(result.Notes).Revisions.Count);
        Assert.Equal(baseline.Attachments[0].Id, result.Attachments[0].Id);
    }

    [Fact]
    public void InvalidVaultReferencesAreRejectedBeforeArchivePublication()
    {
        NoteRecord note = NoteRecord.Create("note.md", "body");
        AttachmentRecord other = AttachmentRecord.Create("other.png", [1]);
        AttachmentReference invalid = new AttachmentReference(note.Id, Guid.NewGuid());
        Assert.Throws<ArgumentException>(() =>
            new KnowledgeCollection([note], [other], [], [invalid]));
        Assert.Throws<ArgumentException>(() =>
            new KnowledgeCollection([note], [other], [],
                [new AttachmentReference(note.Id, other.Id),
                 new AttachmentReference(note.Id, other.Id)]));
    }

    private string Vault()
    {
        string path = Path.Combine(root, "vault");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Put(string directory, string relative, string content)
    {
        string path = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(content));
    }

    private static KnowledgeCollection Read(string path)
    {
        using FileStream input = File.OpenRead(path);
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
