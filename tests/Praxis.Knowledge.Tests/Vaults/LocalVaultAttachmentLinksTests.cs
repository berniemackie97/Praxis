using Praxis.Knowledge.Model;
using Praxis.Knowledge.Vaults;
using Xunit;

namespace Praxis.Knowledge.Tests.Vaults;

public sealed class LocalVaultAttachmentLinksTests
{
    [Fact]
    public void ResolvesRelativeWikiEmbedToStableAttachmentId()
    {
        NoteRecord note = NoteRecord.Create("Notes/first.md", "![[../media/photo.png|300]]");
        AttachmentRecord image = AttachmentRecord.Create("media/photo.png", [1, 2]);
        VaultLinkReport report = LocalVaultAttachmentLinks.Inspect(new KnowledgeCollection([note], [image], []));

        Assert.Empty(report.Problems);
        Assert.Equal(new AttachmentReference(note.Id, image.Id), Assert.Single(report.References));
    }

    [Fact]
    public void ResolvesVaultRootWikiPathAndUniqueShortName()
    {
        NoteRecord note = NoteRecord.Create("Notes/a.md", "![[media/file.pdf]] ![[file.pdf]]");
        AttachmentRecord attachment = AttachmentRecord.Create("media/file.pdf", [3]);
        VaultLinkReport report = LocalVaultAttachmentLinks.Inspect(
            new KnowledgeCollection([note], [attachment], []));

        Assert.Empty(report.Problems);
        Assert.Equal(new AttachmentReference(note.Id, attachment.Id),
            Assert.Single(report.References));
    }

    [Fact]
    public void ResolvesPercentEncodedRelativeMarkdownImage()
    {
        NoteRecord note = NoteRecord.Create("Notes/a.md", "![caption](../media/my%20image.png)");
        AttachmentRecord attachment = AttachmentRecord.Create("media/my image.png", [4]);
        VaultLinkReport report = LocalVaultAttachmentLinks.Inspect(
            new KnowledgeCollection([note], [attachment], []));

        Assert.Empty(report.Problems);
        Assert.Equal(attachment.Id, Assert.Single(report.References).AttachmentId);
    }

    [Fact]
    public void ReportsAmbiguousBareNameWithoutGuessing()
    {
        NoteRecord note = NoteRecord.Create("Notes/a.md", "![[logo.png]]");
        AttachmentRecord first = AttachmentRecord.Create("a/logo.png", [1]);
        AttachmentRecord second = AttachmentRecord.Create("b/logo.png", [2]);
        VaultLinkReport report = LocalVaultAttachmentLinks.Inspect(
            new KnowledgeCollection([note], [first, second], []));

        Assert.Empty(report.References);
        Assert.Contains("Multiple", Assert.Single(report.Problems).Reason);
    }

    [Fact]
    public void ReportsMissingAttachmentWithoutDroppingExistingReferences()
    {
        NoteRecord note = NoteRecord.Create("note.md", "![[deleted.png]]");
        AttachmentRecord retained = AttachmentRecord.Create("retained.png", [2]);
        KnowledgeCollection collection = new([note], [retained],
            [new AttachmentReference(note.Id, retained.Id)]);

        VaultLinkReport report = LocalVaultAttachmentLinks.Inspect(collection);

        Assert.Empty(report.References);
        Assert.Single(report.Problems);
        Assert.Equal(new AttachmentReference(note.Id, retained.Id),
            Assert.Single(collection.References));
    }

    [Fact]
    public void RejectsRelativeEscapeRatherThanLookingOutsideVault()
    {
        NoteRecord note = NoteRecord.Create("Notes/a.md", "![[../../outside.png]]");
        VaultLinkReport report = LocalVaultAttachmentLinks.Inspect(
            new KnowledgeCollection([note], [], []));

        Assert.Empty(report.References);
        Assert.Contains("escapes", Assert.Single(report.Problems).Reason);
    }

    [Fact]
    public void IgnoresExternalImagesAndNonEmbeddedLinks()
    {
        NoteRecord note = NoteRecord.Create("note.md",
            "![external](https://example.test/image.png)\n![cdn](//example.test/image.png)\n[[missing.png]]\n[link](missing.png)");
        VaultLinkReport report = LocalVaultAttachmentLinks.Inspect(
            new KnowledgeCollection([note], [], []));

        Assert.Empty(report.References);
        Assert.Empty(report.Problems);
    }

    [Fact]
    public void IgnoresCodeFencesAndInlineCode()
    {
        string markdown = "~~~markdown\n![[missing.png]]\n~~~\n" +
                          "\u0060![[missing.png]]\u0060\n" +
                          "![real](media/real.png)\n";
        NoteRecord note = NoteRecord.Create("note.md", markdown);
        AttachmentRecord attachment = AttachmentRecord.Create("media/real.png", [5]);
        VaultLinkReport report = LocalVaultAttachmentLinks.Inspect(
            new KnowledgeCollection([note], [attachment], []));

        Assert.Empty(report.Problems);
        Assert.Equal(attachment.Id, Assert.Single(report.References).AttachmentId);
    }

    [Fact]
    public void DistinguishesAmbiguousRootAndRelativeMarkdownTargets()
    {
        NoteRecord note = NoteRecord.Create("Notes/note.md", "![x](logo.png)");
        AttachmentRecord atRoot = AttachmentRecord.Create("logo.png", [1]);
        AttachmentRecord nextToNote = AttachmentRecord.Create("Notes/logo.png", [2]);
        VaultLinkReport report = LocalVaultAttachmentLinks.Inspect(
            new KnowledgeCollection([note], [atRoot, nextToNote], []));

        Assert.Empty(report.References);
        Assert.Single(report.Problems);
    }

    [Fact]
    public void IgnoresNoteEmbedsWhileAuditingBinaryEmbeds()
    {
        NoteRecord note = NoteRecord.Create("index.md",
            "![[other]]\n![[other.md#section]]\n![[image.png]]");
        NoteRecord linkedNote = NoteRecord.Create("other.md", "body");
        AttachmentRecord image = AttachmentRecord.Create("image.png", [1]);
        VaultLinkReport report = LocalVaultAttachmentLinks.Inspect(
            new KnowledgeCollection([note, linkedNote], [image], []));

        Assert.Empty(report.Problems);
        Assert.Equal(new AttachmentReference(note.Id, image.Id),
            Assert.Single(report.References));
    }

    [Fact]
    public void DoesNotModifySourceMarkdownOrCollection()
    {
        const string body = "![[media/old.png]]\r\n";
        NoteRecord note = NoteRecord.Create("note.md", body);
        AttachmentRecord attachment = AttachmentRecord.Create("media/old.png", [2]);
        KnowledgeCollection collection = new([note], [attachment], []);
        VaultLinkReport report = LocalVaultAttachmentLinks.Inspect(collection);

        Assert.Single(report.References);
        Assert.Empty(collection.References);
        Assert.Equal(body, Assert.Single(collection.Notes).Current.Markdown);
        Assert.Single(Assert.Single(collection.Notes).Revisions);
    }
}
