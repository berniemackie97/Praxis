using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Praxis.Knowledge;
using Xunit;

namespace Praxis.Knowledge.Tests;

public sealed class CollectionArchiveTests
{
    [Fact]
    public void RoundTripPreservesNotesBinaryAttachmentsAndStableReferences()
    {
        var note = NoteRecord.Create("Ideas/first.md", "# First\r\n")
            .Revise("Archive/renamed.md", "# Second\n");
        var original = new byte[] { 0, 1, 255, 0, 7, 42 };
        var attachment = AttachmentRecord.Create("images/original.png", original);
        var renamed = attachment.Rename("Images/renamed.png");
        var link = new AttachmentReference(note.Id, renamed.Id);

        original[0] = 99;
        using var archive = Export(new KnowledgeCollection([note], [renamed], [link]));
        var restored = NoteArchive.ReadCollection(archive);

        var restoredNote = Assert.Single(restored.Notes);
        var restoredAttachment = Assert.Single(restored.Attachments);
        Assert.Equal(note.Id, restoredNote.Id);
        Assert.Equal(note.Revisions.ToArray(), restoredNote.Revisions.ToArray());
        Assert.Equal(attachment.Id, restoredAttachment.Id);
        Assert.Equal("Images/renamed.png", restoredAttachment.Path);
        Assert.Equal([0, 1, 255, 0, 7, 42], restoredAttachment.ReadContent());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(restoredAttachment.ReadContent())),
            restoredAttachment.Sha256);
        Assert.Equal(link, Assert.Single(restored.References));
        Assert.Equal(attachment.Id, renamed.Id);
        Assert.Equal("images/original.png", attachment.Path);
    }

    [Fact]
    public void IdenticalBinaryDataIsStoredOnceWithoutMergingAttachmentIdentities()
    {
        var note = NoteRecord.Create("note.md", "two images");
        var content = new byte[] { 10, 20, 30 };
        var first = AttachmentRecord.Create("assets/a.png", content);
        var second = AttachmentRecord.Create("assets/b.png", content);
        var collection = new KnowledgeCollection([note], [first, second],
            [new(note.Id, first.Id), new(note.Id, second.Id)]);

        using var archive = Export(collection);
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.Single(zip.Entries, item => item.FullName.StartsWith("attachments/", StringComparison.Ordinal));
        }

        archive.Position = 0;
        var restored = NoteArchive.ReadCollection(archive);
        Assert.Equal(2, restored.Attachments.Count);
        Assert.NotEqual(restored.Attachments[0].Id, restored.Attachments[1].Id);
        Assert.Equal(first.Sha256, second.Sha256);
        Assert.Equal(content, restored.Attachments[0].ReadContent());
        Assert.Equal(content, restored.Attachments[1].ReadContent());
        Assert.Equal(2, restored.References.Count);
    }

    [Fact]
    public void EmptyOrUnreferencedAttachmentsArePreserved()
    {
        var empty = AttachmentRecord.Create("assets/empty.bin", []);
        var orphan = AttachmentRecord.Create("assets/orphan.bin", [1, 2]);
        using var archive = Export(new KnowledgeCollection([], [empty, orphan], []));

        var result = NoteArchive.ReadCollection(archive);

        Assert.Empty(result.Notes);
        Assert.Empty(result.References);
        Assert.Equal(2, result.Attachments.Count);
        Assert.Empty(Assert.Single(result.Attachments, x => x.Id == empty.Id).ReadContent());
        Assert.Equal([1, 2], Assert.Single(result.Attachments, x => x.Id == orphan.Id).ReadContent());
    }

    [Fact]
    public void LegacyArchiveCanBeReadAsCollectionWithoutAttachments()
    {
        var note = NoteRecord.Create("legacy.md", "# Original");
        using var archive = new MemoryStream();
        NoteArchive.Write(archive, [note]);
        archive.Position = 0;

        var restored = NoteArchive.ReadCollection(archive);

        Assert.Equal(note.Id, Assert.Single(restored.Notes).Id);
        Assert.Empty(restored.Attachments);
        Assert.Empty(restored.References);
        archive.Position = 0;
        Assert.Equal(note.Id, Assert.Single(NoteArchive.Read(archive)).Id);
    }

    [Fact]
    public void NoteOnlyReaderCannotSilentlyDiscardAttachments()
    {
        var note = NoteRecord.Create("note.md", "body");
        using var archive = Export(new KnowledgeCollection([note],
            [AttachmentRecord.Create("attachment.bin", [1])], []));

        Assert.Throws<NotSupportedException>(() => NoteArchive.Read(archive));
    }

    [Fact]
    public void CollectionArchiveKeepsCallerStreamOpen()
    {
        using var archive = Export(new KnowledgeCollection([], [], []));
        Assert.True(archive.CanRead);
        Assert.Empty(NoteArchive.ReadCollection(archive).Notes);
        Assert.True(archive.CanRead);
    }

    [Fact]
    public void SourceBytesCannotBeMutatedThroughAttachmentRecord()
    {
        var source = new byte[] { 9, 8, 7 };
        var attachment = AttachmentRecord.Create("bytes.bin", source);
        source[0] = 1;
        var copy = attachment.ReadContent();
        copy[1] = 1;

        Assert.Equal([9, 8, 7], attachment.ReadContent());
        Assert.Equal(attachment.Id, attachment.Rename("new.bin").Id);
        Assert.Equal(attachment.Sha256, attachment.Rename("new.bin").Sha256);
    }

    [Theory]
    [InlineData("../escape.png")]
    [InlineData("/absolute.bin")]
    [InlineData("images\\windows.png")]
    [InlineData("bad//name.png")]
    [InlineData("assets/CON.png")]
    [InlineData("assets/bad?.png")]
    [InlineData("assets/trailing /file.png")]
    [InlineData("assets/e\u0301.png")]
    public void InvalidAttachmentPathsAreRejected(string path)
    {
        Assert.Throws<ArgumentException>(() => AttachmentRecord.Create(path, [1]));
    }

    [Fact]
    public void DuplicatePathsAcrossNotesAndAttachmentsAreRejected()
    {
        var note = NoteRecord.Create("Notes/entry.md", "content");
        var attachment = AttachmentRecord.Create("notes/ENTRY.md", [1]);

        Assert.Throws<ArgumentException>(() =>
            new KnowledgeCollection([note], [attachment], []));
    }

    [Fact]
    public void MissingOrDuplicateReferencesAreRejectedBeforeExport()
    {
        var note = NoteRecord.Create("note.md", "content");
        var attachment = AttachmentRecord.Create("asset.png", [1]);
        var link = new AttachmentReference(note.Id, attachment.Id);

        Assert.Throws<ArgumentException>(() =>
            new KnowledgeCollection([note], [], [link]));
        Assert.Throws<ArgumentException>(() =>
            new KnowledgeCollection([], [attachment], [link]));
        Assert.Throws<ArgumentException>(() =>
            new KnowledgeCollection([note], [attachment], [link, link]));
    }

    [Fact]
    public void OversizedAttachmentIsRejectedBeforeWriting()
    {
        var bytes = new byte[32 * 1024 * 1024 + 1];
        var attachment = AttachmentRecord.Create("large.bin", bytes);
        using var archive = new MemoryStream();

        Assert.Throws<InvalidDataException>(() =>
            NoteArchive.WriteCollection(archive, new KnowledgeCollection([], [attachment], [])));
        Assert.Equal(0, archive.Length);
    }

    [Fact]
    public void TamperedBinaryContentIsRejected()
    {
        var attachment = AttachmentRecord.Create("image.png", [1, 2, 3, 4]);
        using var archive = Export(new KnowledgeCollection([], [attachment], []));
        ArchiveFixture.ReplaceEntry(archive, $"attachments/{attachment.Sha256}", _ => [1, 2, 3, 5]);

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void MissingBinaryPayloadIsRejected()
    {
        var attachment = AttachmentRecord.Create("image.png", [1, 2, 3]);
        using var archive = Export(new KnowledgeCollection([], [attachment], []));
        RemoveEntry(archive, $"attachments/{attachment.Sha256}");

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void UnlistedArchivePayloadIsRejected()
    {
        using var archive = Export(new KnowledgeCollection([], [], []));
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Update, leaveOpen: true))
        {
            using var extra = zip.CreateEntry("../unlisted").Open();
            extra.WriteByte(1);
        }

        archive.Position = 0;
        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void TamperedAttachmentPathIsRejected()
    {
        var attachment = AttachmentRecord.Create("valid.bin", [1]);
        using var archive = Export(new KnowledgeCollection([], [attachment], []));
        EditManifest(archive, json => json["attachments"]!.AsArray()[0]!["path"] = "../escape.bin");

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void TamperedAttachmentDigestIsRejected()
    {
        var attachment = AttachmentRecord.Create("valid.bin", [1, 2]);
        using var archive = Export(new KnowledgeCollection([], [attachment], []));
        EditManifest(archive, json =>
            json["attachments"]!.AsArray()[0]!["sha256"] = new string('0', 64));

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void TamperedAttachmentReferenceIsRejected()
    {
        var note = NoteRecord.Create("note.md", "");
        var attachment = AttachmentRecord.Create("valid.bin", [1]);
        var reference = new AttachmentReference(note.Id, attachment.Id);
        using var archive = Export(new KnowledgeCollection([note], [attachment], [reference]));
        EditManifest(archive, json => json["references"]!.AsArray()[0]!["noteId"] = Guid.NewGuid());

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void DuplicateReferencesInManifestAreRejected()
    {
        var note = NoteRecord.Create("note.md", "");
        var attachment = AttachmentRecord.Create("valid.bin", [1]);
        using var archive = Export(new KnowledgeCollection([note], [attachment],
            [new(note.Id, attachment.Id)]));
        EditManifest(archive, json =>
        {
            var links = json["references"]!.AsArray();
            links.Add(links[0]!.DeepClone());
        });

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void DuplicateAttachmentPathsInManifestAreRejected()
    {
        var first = AttachmentRecord.Create("a.png", [1]);
        var second = AttachmentRecord.Create("b.png", [2]);
        using var archive = Export(new KnowledgeCollection([], [first, second], []));
        EditManifest(archive, json =>
        {
            var attachments = json["attachments"]!.AsArray();
            var firstPath = attachments[0]!["path"]!.GetValue<string>();
            attachments[1]!["path"] = firstPath.ToUpperInvariant();
        });

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void UnknownCollectionVersionIsRejected()
    {
        using var archive = Export(new KnowledgeCollection([], [], []));
        EditManifest(archive, json => json["formatVersion"] = 4);

        Assert.Throws<NotSupportedException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void VersionThreeRequiresVaultReferenceMetadataEvenWhenEmpty()
    {
        var note = NoteRecord.Create("note.md", "plain");
        using var archive = Export(new KnowledgeCollection([note], [], [], []));
        EditManifest(archive, json => json.Remove("vaultReferences"));

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void VersionTwoCannotClaimTrackedVaultLinksWithoutVersionChange()
    {
        using var archive = Export(new KnowledgeCollection([], [], []));
        EditManifest(archive, json => json["vaultReferences"] = new JsonArray());

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void TamperedVaultDerivedReferenceIsRejected()
    {
        var note = NoteRecord.Create("note.md", "![[image.png]]");
        var image = AttachmentRecord.Create("image.png", [2]);
        using var archive = Export(new KnowledgeCollection([note], [image], [],
            [new AttachmentReference(note.Id, image.Id)]));
        EditManifest(archive, json =>
            json["vaultReferences"]!.AsArray()[0]!["attachmentId"] = Guid.NewGuid());

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void DuplicateVaultDerivedReferencesInManifestAreRejected()
    {
        var note = NoteRecord.Create("note.md", "![[image.png]]");
        var image = AttachmentRecord.Create("image.png", [2]);
        using var archive = Export(new KnowledgeCollection([note], [image], [],
            [new AttachmentReference(note.Id, image.Id)]));
        EditManifest(archive, json =>
        {
            var references = json["vaultReferences"]!.AsArray();
            references.Add(references[0]!.DeepClone());
        });

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void NoteOnlyReaderRejectsVersionThreeCollection()
    {
        using var archive = Export(new KnowledgeCollection(
            [NoteRecord.Create("note.md", "body")], [], [], []));

        Assert.Throws<NotSupportedException>(() => NoteArchive.Read(archive));
    }

    [Fact]
    public void ExtraManifestFieldsAreRejected()
    {
        using var archive = Export(new KnowledgeCollection([], [], []));
        EditManifest(archive, json => json["unexpected"] = true);

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void VersionOneCannotContainAttachmentMetadata()
    {
        using var archive = new MemoryStream();
        NoteArchive.Write(archive, [NoteRecord.Create("note.md", "body")]);
        archive.Position = 0;
        EditManifest(archive, json => json["attachments"] = new JsonArray());

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    private static MemoryStream Export(KnowledgeCollection collection)
    {
        var stream = new MemoryStream();
        NoteArchive.WriteCollection(stream, collection);
        stream.Position = 0;
        return stream;
    }

    private static void EditManifest(MemoryStream archive, Action<JsonObject> edit)
    {
        ArchiveFixture.ReplaceEntry(archive, "manifest.json", bytes =>
        {
            var json = JsonNode.Parse(Encoding.UTF8.GetString(bytes))!.AsObject();
            edit(json);
            return Encoding.UTF8.GetBytes(json.ToJsonString());
        });
    }

    private static void RemoveEntry(MemoryStream archive, string name)
    {
        archive.Position = 0;
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Update, leaveOpen: true))
        {
            zip.GetEntry(name)!.Delete();
        }

        archive.Position = 0;
    }

}
