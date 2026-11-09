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
        NoteRecord note = NoteRecord.Create("Ideas/first.md", "# First\r\n")
            .Revise("Archive/renamed.md", "# Second\n");
        byte[] original = new byte[] { 0, 1, 255, 0, 7, 42 };
        AttachmentRecord attachment = AttachmentRecord.Create("images/original.png", original);
        AttachmentRecord renamed = attachment.Rename("Images/renamed.png");
        AttachmentReference link = new AttachmentReference(note.Id, renamed.Id);

        original[0] = 99;
        using MemoryStream archive = Export(new KnowledgeCollection([note], [renamed], [link]));
        KnowledgeCollection restored = NoteArchive.ReadCollection(archive);

        NoteRecord restoredNote = Assert.Single(restored.Notes);
        AttachmentRecord restoredAttachment = Assert.Single(restored.Attachments);
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
        NoteRecord note = NoteRecord.Create("note.md", "two images");
        byte[] content = new byte[] { 10, 20, 30 };
        AttachmentRecord first = AttachmentRecord.Create("assets/a.png", content);
        AttachmentRecord second = AttachmentRecord.Create("assets/b.png", content);
        KnowledgeCollection collection = new KnowledgeCollection([note], [first, second],
            [new(note.Id, first.Id), new(note.Id, second.Id)]);

        using MemoryStream archive = Export(collection);
        using (ZipArchive zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true))
        {
            Assert.Single(zip.Entries, item => item.FullName.StartsWith("attachments/", StringComparison.Ordinal));
        }

        archive.Position = 0;
        KnowledgeCollection restored = NoteArchive.ReadCollection(archive);
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
        AttachmentRecord empty = AttachmentRecord.Create("assets/empty.bin", []);
        AttachmentRecord orphan = AttachmentRecord.Create("assets/orphan.bin", [1, 2]);
        using MemoryStream archive = Export(new KnowledgeCollection([], [empty, orphan], []));

        KnowledgeCollection result = NoteArchive.ReadCollection(archive);

        Assert.Empty(result.Notes);
        Assert.Empty(result.References);
        Assert.Equal(2, result.Attachments.Count);
        Assert.Empty(Assert.Single(result.Attachments, x => x.Id == empty.Id).ReadContent());
        Assert.Equal([1, 2], Assert.Single(result.Attachments, x => x.Id == orphan.Id).ReadContent());
    }

    [Fact]
    public void LegacyArchiveCanBeReadAsCollectionWithoutAttachments()
    {
        NoteRecord note = NoteRecord.Create("legacy.md", "# Original");
        using MemoryStream archive = new MemoryStream();
        NoteArchive.Write(archive, [note]);
        archive.Position = 0;

        KnowledgeCollection restored = NoteArchive.ReadCollection(archive);

        Assert.Equal(note.Id, Assert.Single(restored.Notes).Id);
        Assert.Empty(restored.Attachments);
        Assert.Empty(restored.References);
        archive.Position = 0;
        Assert.Equal(note.Id, Assert.Single(NoteArchive.Read(archive)).Id);
    }

    [Fact]
    public void NoteOnlyReaderCannotSilentlyDiscardAttachments()
    {
        NoteRecord note = NoteRecord.Create("note.md", "body");
        using MemoryStream archive = Export(new KnowledgeCollection([note],
            [AttachmentRecord.Create("attachment.bin", [1])], []));

        Assert.Throws<NotSupportedException>(() => NoteArchive.Read(archive));
    }

    [Fact]
    public void CollectionArchiveKeepsCallerStreamOpen()
    {
        using MemoryStream archive = Export(new KnowledgeCollection([], [], []));
        Assert.True(archive.CanRead);
        Assert.Empty(NoteArchive.ReadCollection(archive).Notes);
        Assert.True(archive.CanRead);
    }

    [Fact]
    public void SourceBytesCannotBeMutatedThroughAttachmentRecord()
    {
        byte[] source = new byte[] { 9, 8, 7 };
        AttachmentRecord attachment = AttachmentRecord.Create("bytes.bin", source);
        source[0] = 1;
        byte[] copy = attachment.ReadContent();
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
        NoteRecord note = NoteRecord.Create("Notes/entry.md", "content");
        AttachmentRecord attachment = AttachmentRecord.Create("notes/ENTRY.md", [1]);

        Assert.Throws<ArgumentException>(() =>
            new KnowledgeCollection([note], [attachment], []));
    }

    [Fact]
    public void MissingOrDuplicateReferencesAreRejectedBeforeExport()
    {
        NoteRecord note = NoteRecord.Create("note.md", "content");
        AttachmentRecord attachment = AttachmentRecord.Create("asset.png", [1]);
        AttachmentReference link = new AttachmentReference(note.Id, attachment.Id);

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
        byte[] bytes = new byte[32 * 1024 * 1024 + 1];
        AttachmentRecord attachment = AttachmentRecord.Create("large.bin", bytes);
        using MemoryStream archive = new MemoryStream();

        Assert.Throws<InvalidDataException>(() =>
            NoteArchive.WriteCollection(archive, new KnowledgeCollection([], [attachment], [])));
        Assert.Equal(0, archive.Length);
    }

    [Fact]
    public void TamperedBinaryContentIsRejected()
    {
        AttachmentRecord attachment = AttachmentRecord.Create("image.png", [1, 2, 3, 4]);
        using MemoryStream archive = Export(new KnowledgeCollection([], [attachment], []));
        ArchiveFixture.ReplaceEntry(archive, $"attachments/{attachment.Sha256}", _ => [1, 2, 3, 5]);

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void MissingBinaryPayloadIsRejected()
    {
        AttachmentRecord attachment = AttachmentRecord.Create("image.png", [1, 2, 3]);
        using MemoryStream archive = Export(new KnowledgeCollection([], [attachment], []));
        RemoveEntry(archive, $"attachments/{attachment.Sha256}");

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void UnlistedArchivePayloadIsRejected()
    {
        using MemoryStream archive = Export(new KnowledgeCollection([], [], []));
        using (ZipArchive zip = new ZipArchive(archive, ZipArchiveMode.Update, leaveOpen: true))
        {
            using Stream extra = zip.CreateEntry("../unlisted").Open();
            extra.WriteByte(1);
        }

        archive.Position = 0;
        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void TamperedAttachmentPathIsRejected()
    {
        AttachmentRecord attachment = AttachmentRecord.Create("valid.bin", [1]);
        using MemoryStream archive = Export(new KnowledgeCollection([], [attachment], []));
        EditManifest(archive, json => json["attachments"]!.AsArray()[0]!["path"] = "../escape.bin");

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void TamperedAttachmentDigestIsRejected()
    {
        AttachmentRecord attachment = AttachmentRecord.Create("valid.bin", [1, 2]);
        using MemoryStream archive = Export(new KnowledgeCollection([], [attachment], []));
        EditManifest(archive, json =>
            json["attachments"]!.AsArray()[0]!["sha256"] = new string('0', 64));

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void TamperedAttachmentReferenceIsRejected()
    {
        NoteRecord note = NoteRecord.Create("note.md", "");
        AttachmentRecord attachment = AttachmentRecord.Create("valid.bin", [1]);
        AttachmentReference reference = new AttachmentReference(note.Id, attachment.Id);
        using MemoryStream archive = Export(new KnowledgeCollection([note], [attachment], [reference]));
        EditManifest(archive, json => json["references"]!.AsArray()[0]!["noteId"] = Guid.NewGuid());

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void DuplicateReferencesInManifestAreRejected()
    {
        NoteRecord note = NoteRecord.Create("note.md", "");
        AttachmentRecord attachment = AttachmentRecord.Create("valid.bin", [1]);
        using MemoryStream archive = Export(new KnowledgeCollection([note], [attachment],
            [new(note.Id, attachment.Id)]));
        EditManifest(archive, json =>
        {
            JsonArray links = json["references"]!.AsArray();
            links.Add(links[0]!.DeepClone());
        });

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void DuplicateAttachmentPathsInManifestAreRejected()
    {
        AttachmentRecord first = AttachmentRecord.Create("a.png", [1]);
        AttachmentRecord second = AttachmentRecord.Create("b.png", [2]);
        using MemoryStream archive = Export(new KnowledgeCollection([], [first, second], []));
        EditManifest(archive, json =>
        {
            JsonArray attachments = json["attachments"]!.AsArray();
            string firstPath = attachments[0]!["path"]!.GetValue<string>();
            attachments[1]!["path"] = firstPath.ToUpperInvariant();
        });

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void UnknownCollectionVersionIsRejected()
    {
        using MemoryStream archive = Export(new KnowledgeCollection([], [], []));
        EditManifest(archive, json => json["formatVersion"] = 4);

        Assert.Throws<NotSupportedException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void VersionThreeRequiresVaultReferenceMetadataEvenWhenEmpty()
    {
        NoteRecord note = NoteRecord.Create("note.md", "plain");
        using MemoryStream archive = Export(new KnowledgeCollection([note], [], [], []));
        EditManifest(archive, json => json.Remove("vaultReferences"));

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void VersionTwoCannotClaimTrackedVaultLinksWithoutVersionChange()
    {
        using MemoryStream archive = Export(new KnowledgeCollection([], [], []));
        EditManifest(archive, json => json["vaultReferences"] = new JsonArray());

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void TamperedVaultDerivedReferenceIsRejected()
    {
        NoteRecord note = NoteRecord.Create("note.md", "![[image.png]]");
        AttachmentRecord image = AttachmentRecord.Create("image.png", [2]);
        using MemoryStream archive = Export(new KnowledgeCollection([note], [image], [],
            [new AttachmentReference(note.Id, image.Id)]));
        EditManifest(archive, json =>
            json["vaultReferences"]!.AsArray()[0]!["attachmentId"] = Guid.NewGuid());

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void DuplicateVaultDerivedReferencesInManifestAreRejected()
    {
        NoteRecord note = NoteRecord.Create("note.md", "![[image.png]]");
        AttachmentRecord image = AttachmentRecord.Create("image.png", [2]);
        using MemoryStream archive = Export(new KnowledgeCollection([note], [image], [],
            [new AttachmentReference(note.Id, image.Id)]));
        EditManifest(archive, json =>
        {
            JsonArray references = json["vaultReferences"]!.AsArray();
            references.Add(references[0]!.DeepClone());
        });

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void NoteOnlyReaderRejectsVersionThreeCollection()
    {
        using MemoryStream archive = Export(new KnowledgeCollection(
            [NoteRecord.Create("note.md", "body")], [], [], []));

        Assert.Throws<NotSupportedException>(() => NoteArchive.Read(archive));
    }

    [Fact]
    public void ExtraManifestFieldsAreRejected()
    {
        using MemoryStream archive = Export(new KnowledgeCollection([], [], []));
        EditManifest(archive, json => json["unexpected"] = true);

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    [Fact]
    public void VersionOneCannotContainAttachmentMetadata()
    {
        using MemoryStream archive = new MemoryStream();
        NoteArchive.Write(archive, [NoteRecord.Create("note.md", "body")]);
        archive.Position = 0;
        EditManifest(archive, json => json["attachments"] = new JsonArray());

        Assert.Throws<InvalidDataException>(() => NoteArchive.ReadCollection(archive));
    }

    private static MemoryStream Export(KnowledgeCollection collection)
    {
        MemoryStream stream = new MemoryStream();
        NoteArchive.WriteCollection(stream, collection);
        stream.Position = 0;
        return stream;
    }

    private static void EditManifest(MemoryStream archive, Action<JsonObject> edit)
    {
        ArchiveFixture.ReplaceEntry(archive, "manifest.json", bytes =>
        {
            JsonObject json = JsonNode.Parse(Encoding.UTF8.GetString(bytes))!.AsObject();
            edit(json);
            return Encoding.UTF8.GetBytes(json.ToJsonString());
        });
    }

    private static void RemoveEntry(MemoryStream archive, string name)
    {
        archive.Position = 0;
        using (ZipArchive zip = new ZipArchive(archive, ZipArchiveMode.Update, leaveOpen: true))
        {
            zip.GetEntry(name)!.Delete();
        }

        archive.Position = 0;
    }

}
