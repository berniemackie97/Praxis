using Praxis.Knowledge;
using Xunit;

namespace Praxis.Knowledge.Tests;

public sealed class LocalVaultAttachmentLinksTests
{
    [Fact]
    public void ResolvesRelativeWikiEmbedToStableAttachmentId()
    {
        var note = NoteRecord.Create("Notes/first.md", "![[../media/photo.png|300]]");
        var image = AttachmentRecord.Create("media/photo.png", [1, 2]);
        var report = LocalVaultAttachmentLinks.Inspect(new KnowledgeCollection([note], [image], []));

        Assert.Empty(report.Problems);
        Assert.Equal(new AttachmentReference(note.Id, image.Id), Assert.Single(report.References));
    }

    [Fact]
    public void ResolvesVaultRootWikiPathAndUniqueShortName()
    {
        var note = NoteRecord.Create("Notes/a.md", "![[media/file.pdf]] ![[file.pdf]]");
        var attachment = AttachmentRecord.Create("media/file.pdf", [3]);
        var report = LocalVaultAttachmentLinks.Inspect(
            new KnowledgeCollection([note], [attachment], []));

        Assert.Empty(report.Problems);
        Assert.Equal(new AttachmentReference(note.Id, attachment.Id),
            Assert.Single(report.References));
    }

    [Fact]
    public void ResolvesPercentEncodedRelativeMarkdownImage()
    {
        var note = NoteRecord.Create("Notes/a.md", "![caption](../media/my%20image.png)");
        var attachment = AttachmentRecord.Create("media/my image.png", [4]);
        var report = LocalVaultAttachmentLinks.Inspect(
            new KnowledgeCollection([note], [attachment], []));

        Assert.Empty(report.Problems);
        Assert.Equal(attachment.Id, Assert.Single(report.References).AttachmentId);
    }

    [Fact]
    public void ReportsAmbiguousBareNameWithoutGuessing()
    {
        var note = NoteRecord.Create("Notes/a.md", "![[logo.png]]");
        var first = AttachmentRecord.Create("a/logo.png", [1]);
        var second = AttachmentRecord.Create("b/logo.png", [2]);
        var report = LocalVaultAttachmentLinks.Inspect(
            new KnowledgeCollection([note], [first, second], []));

        Assert.Empty(report.References);
        Assert.Contains("Multiple", Assert.Single(report.Problems).Reason);
    }

    [Fact]
    public void ReportsMissingAttachmentWithoutDroppingExistingReferences()
    {
        var note = NoteRecord.Create("note.md", "![[deleted.png]]");
        var retained = AttachmentRecord.Create("retained.png", [2]);
        var collection = new KnowledgeCollection([note], [retained],
            [new AttachmentReference(note.Id, retained.Id)]);

        var report = LocalVaultAttachmentLinks.Inspect(collection);

        Assert.Empty(report.References);
        Assert.Single(report.Problems);
        Assert.Equal(new AttachmentReference(note.Id, retained.Id),
            Assert.Single(collection.References));
    }

    [Fact]
    public void RejectsRelativeEscapeRatherThanLookingOutsideVault()
    {
        var note = NoteRecord.Create("Notes/a.md", "![[../../outside.png]]");
        var report = LocalVaultAttachmentLinks.Inspect(
            new KnowledgeCollection([note], [], []));

        Assert.Empty(report.References);
        Assert.Contains("escapes", Assert.Single(report.Problems).Reason);
    }

    [Fact]
    public void IgnoresExternalImagesAndNonEmbeddedLinks()
    {
        var note = NoteRecord.Create("note.md",
            "![external](https://example.test/image.png)\n[[missing.png]]\n[link](missing.png)");
        var report = LocalVaultAttachmentLinks.Inspect(
            new KnowledgeCollection([note], [], []));

        Assert.Empty(report.References);
        Assert.Empty(report.Problems);
    }

    [Fact]
    public void IgnoresCodeFencesAndInlineCode()
    {
        var markdown = "~~~markdown\n![[missing.png]]\n~~~\n" +
            "\u0060![[missing.png]]\u0060\n" +
            "![real](media/real.png)\n";
        var note = NoteRecord.Create("note.md", markdown);
        var attachment = AttachmentRecord.Create("media/real.png", [5]);
        var report = LocalVaultAttachmentLinks.Inspect(
            new KnowledgeCollection([note], [attachment], []));

        Assert.Empty(report.Problems);
        Assert.Equal(attachment.Id, Assert.Single(report.References).AttachmentId);
    }

    [Fact]
    public void DistinguishesAmbiguousRootAndRelativeMarkdownTargets()
    {
        var note = NoteRecord.Create("Notes/note.md", "![x](logo.png)");
        var atRoot = AttachmentRecord.Create("logo.png", [1]);
        var nextToNote = AttachmentRecord.Create("Notes/logo.png", [2]);
        var report = LocalVaultAttachmentLinks.Inspect(
            new KnowledgeCollection([note], [atRoot, nextToNote], []));

        Assert.Empty(report.References);
        Assert.Single(report.Problems);
    }

    [Fact]
    public void IgnoresNoteEmbedsWhileAuditingBinaryEmbeds()
    {
        var note = NoteRecord.Create("index.md",
            "![[other]]\n![[other.md#section]]\n![[image.png]]");
        var linkedNote = NoteRecord.Create("other.md", "body");
        var image = AttachmentRecord.Create("image.png", [1]);
        var report = LocalVaultAttachmentLinks.Inspect(
            new KnowledgeCollection([note, linkedNote], [image], []));

        Assert.Empty(report.Problems);
        Assert.Equal(new AttachmentReference(note.Id, image.Id),
            Assert.Single(report.References));
    }

    [Fact]
    public void DoesNotModifySourceMarkdownOrCollection()
    {
        const string body = "![[media/old.png]]\r\n";
        var note = NoteRecord.Create("note.md", body);
        var attachment = AttachmentRecord.Create("media/old.png", [2]);
        var collection = new KnowledgeCollection([note], [attachment], []);
        var report = LocalVaultAttachmentLinks.Inspect(collection);

        Assert.Single(report.References);
        Assert.Empty(collection.References);
        Assert.Equal(body, Assert.Single(collection.Notes).Current.Markdown);
        Assert.Single(Assert.Single(collection.Notes).Revisions);
    }
}
