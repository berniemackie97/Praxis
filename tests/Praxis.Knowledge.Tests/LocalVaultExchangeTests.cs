using System.IO.Compression;
using System.Text;
using Praxis.Knowledge;
using Xunit;

namespace Praxis.Knowledge.Tests;

public sealed class LocalVaultExchangeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(),
        "praxis-vault-" + Guid.NewGuid().ToString("N"));

    public LocalVaultExchangeTests() => Directory.CreateDirectory(root);

    [Fact]
    public void RepresentativeVaultRoundTripsNestedNotesLinksAttachmentsAndHiddenConfiguration()
    {
        string source = Folder("source");
        byte[] markdown = Encoding.UTF8.GetBytes(
            "---\r\ntitle: Réunion\r\n---\r\n# First\r\n![[media/photo.png]]\r\n" +
            "![Preview](../media/photo.png)\r\n[[Notes/second]]\r\n");
        byte[] other = Encoding.UTF8.GetBytes("# Second\n\n[link](first.md)\n");
        byte[] binary = new byte[] { 0, 255, 1, 2, 0, 16, 42 };
        Put(source, "Notes/first.md", markdown);
        Put(source, "Notes/second.md", other);
        Put(source, "media/photo.png", binary);
        Put(source, ".obsidian/app.json", Encoding.UTF8.GetBytes("{\"theme\":\"dark\"}"));
        Put(source, "media/empty.bin", []);

        string archive = Path.Combine(root, "complete.zip");
        LocalVaultExchange.CaptureArchive(source, archive);
        using (FileStream stream = File.OpenRead(archive))
        {
            KnowledgeCollection collection = NoteArchive.ReadCollection(stream);
            Assert.Equal(2, collection.Notes.Count);
            Assert.Equal(3, collection.Attachments.Count);
            Assert.Empty(collection.References);
            Assert.Equal(Encoding.UTF8.GetString(markdown),
                Assert.Single(collection.Notes, note => note.Current.Path == "Notes/first.md")
                    .Current.Markdown);
            Assert.Equal(binary,
                Assert.Single(collection.Attachments, attachment =>
                    attachment.Path == "media/photo.png").ReadContent());
        }

        string destination = Path.Combine(root, "restored");
        LocalVaultExchange.RestoreArchive(archive, destination);
        AssertFilesEqual(source, destination);
        Assert.Empty(Directory.EnumerateFiles(root, "*.pending"));
        Assert.DoesNotContain(Directory.EnumerateDirectories(root), path =>
            Path.GetFileName(path).Contains(".restore", StringComparison.Ordinal));
    }

    [Fact]
    public void ArchiveRetainsHistoricalRevisionsWhileFolderRestoresOnlyCurrentContent()
    {
        NoteRecord note = NoteRecord.Create("old.md", "# First\n")
            .Revise("Notes/new.md", "# Second\r\n");
        AttachmentRecord attachment = AttachmentRecord.Create("media/file.bin", [1, 2, 3]);
        KnowledgeCollection collection = new KnowledgeCollection([note], [attachment],
            [new AttachmentReference(note.Id, attachment.Id)]);
        string archive = Path.Combine(root, "history.zip");
        using (FileStream stream = File.Create(archive))
        {
            NoteArchive.WriteCollection(stream, collection);
        }

        string destination = Path.Combine(root, "current");
        LocalVaultExchange.RestoreArchive(archive, destination);
        Assert.Equal("# Second\r\n", File.ReadAllText(Path.Combine(destination, "Notes/new.md")));
        Assert.False(File.Exists(Path.Combine(destination, "old.md")));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(Path.Combine(destination, "media/file.bin")));

        using FileStream read = File.OpenRead(archive);
        KnowledgeCollection preserved = NoteArchive.ReadCollection(read);
        Assert.Equal(note.Revisions.ToArray(), Assert.Single(preserved.Notes).Revisions.ToArray());
        Assert.Equal(Assert.Single(collection.References), Assert.Single(preserved.References));
    }

    [Fact]
    public void RestoreCollectionRejectsConflictingFileDirectoryPathsBeforeWriting()
    {
        NoteRecord note = NoteRecord.Create("assets.md", "note");
        AttachmentRecord attachment = AttachmentRecord.Create("assets.md/photo.png", [1]);
        string destination = Path.Combine(root, "blocked");

        Assert.Throws<InvalidDataException>(() => LocalVaultExchange.RestoreCollection(
            new KnowledgeCollection([note], [attachment], []), destination));
        Assert.False(Path.Exists(destination));
    }

    [Fact]
    public void RestoreRejectsDirectoryCaseVariantsBeforeCreatingTarget()
    {
        NoteRecord first = NoteRecord.Create("Notes/one.md", "one");
        NoteRecord second = NoteRecord.Create("notes/two.md", "two");
        string destination = Path.Combine(root, "case-folded");

        Assert.Throws<InvalidDataException>(() =>
            LocalVaultExchange.RestoreCollection(new KnowledgeCollection(
                [first, second], [], []), destination));
        Assert.False(Path.Exists(destination));
    }

    [Fact]
    public void ExistingDestinationIsNeverOverwritten()
    {
        string original = Folder("original");
        Put(original, "note.md", Encoding.UTF8.GetBytes("new"));
        string archive = Path.Combine(root, "archive.zip");
        LocalVaultExchange.CaptureArchive(original, archive);
        string existing = Folder("existing");
        Put(existing, "note.md", Encoding.UTF8.GetBytes("old"));

        Assert.Throws<IOException>(() => LocalVaultExchange.RestoreArchive(archive, existing));
        Assert.Equal("old", File.ReadAllText(Path.Combine(existing, "note.md")));
        Assert.Single(Directory.EnumerateFiles(existing));
    }

    [Fact]
    public void ExistingArchiveIsNeverOverwritten()
    {
        string source = Folder("source");
        Put(source, "note.md", Encoding.UTF8.GetBytes("new"));
        string destination = Path.Combine(root, "archive.zip");
        File.WriteAllBytes(destination, [1, 2, 3]);

        Assert.Throws<IOException>(() => LocalVaultExchange.CaptureArchive(source, destination));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(destination));
    }

    [Fact]
    public void ArchiveCannotBePlacedInsideItsSourceVault()
    {
        string source = Folder("source");
        Put(source, "note.md", Encoding.UTF8.GetBytes("original"));

        Assert.Throws<ArgumentException>(() =>
            LocalVaultExchange.CaptureArchive(source, Path.Combine(source, "snapshot.zip")));
        Assert.Single(Directory.EnumerateFiles(source));
    }

    [Fact]
    public void MalformedArchiveDoesNotCreateRestorationTarget()
    {
        string archive = Path.Combine(root, "bad.zip");
        File.WriteAllBytes(archive, [1, 2, 3, 4]);
        string target = Path.Combine(root, "not-created");

        Assert.ThrowsAny<InvalidDataException>(() =>
            LocalVaultExchange.RestoreArchive(archive, target));
        Assert.False(Path.Exists(target));
    }

    [Fact]
    public void TamperedAttachmentFailsBeforeAnyRestoredFilesArePublished()
    {
        string source = Folder("source");
        Put(source, "note.md", Encoding.UTF8.GetBytes("private"));
        Put(source, "media/original.bin", [1, 2, 3]);
        string archive = Path.Combine(root, "modified.zip");
        LocalVaultExchange.CaptureArchive(source, archive);
        string entry;
        using (FileStream stream = File.OpenRead(archive))
        {
            using ZipArchive zip = new ZipArchive(stream, ZipArchiveMode.Read);
            entry = Assert.Single(zip.Entries, item =>
                item.FullName.StartsWith("attachments/", StringComparison.Ordinal)).FullName;
        }

        using (MemoryStream buffer = new MemoryStream())
        {
            buffer.Write(File.ReadAllBytes(archive));
            buffer.Position = 0;
            ArchiveFixture.ReplaceEntry(buffer, entry, _ => [1, 2, 99]);
            File.WriteAllBytes(archive, buffer.ToArray());
        }

        string destination = Path.Combine(root, "never-published");
        Assert.Throws<InvalidDataException>(() =>
            LocalVaultExchange.RestoreArchive(archive, destination));
        Assert.False(Path.Exists(destination));
    }

    [Fact]
    public void InvalidMarkdownUtf8IsRejectedWithoutCreatingArchive()
    {
        string source = Folder("source");
        Put(source, "invalid.md", [0xc3, 0x28]);
        string archive = Path.Combine(root, "invalid.zip");

        Assert.Throws<InvalidDataException>(() =>
            LocalVaultExchange.CaptureArchive(source, archive));
        Assert.False(Path.Exists(archive));
    }

    [Fact]
    public void OversizedMarkdownIsRejectedBeforeAllocationOrPublication()
    {
        string source = Folder("source");
        Put(source, "too-large.md", new byte[8 * 1024 * 1024 + 1]);

        Assert.Throws<InvalidDataException>(() =>
            LocalVaultExchange.CaptureArchive(source, Path.Combine(root, "too-large.zip")));
        Assert.False(Path.Exists(Path.Combine(root, "too-large.zip")));
    }

    [Fact]
    public void OversizedAttachmentIsRejected()
    {
        string source = Folder("source");
        Put(source, "too-large.bin", new byte[32 * 1024 * 1024 + 1]);

        Assert.Throws<InvalidDataException>(() =>
            LocalVaultExchange.Capture(source));
    }

    [Fact]
    public void EmptyNestedDirectoryIsRejectedRatherThanSilentlyLost()
    {
        string source = Folder("source");
        Directory.CreateDirectory(Path.Combine(source, "empty"));

        Assert.Throws<InvalidDataException>(() => LocalVaultExchange.Capture(source));
    }

    [Fact]
    public void EmptyVaultCanRoundTrip()
    {
        string source = Folder("source");
        string archive = Path.Combine(root, "empty.zip");
        LocalVaultExchange.CaptureArchive(source, archive);
        string output = Path.Combine(root, "empty-restore");
        LocalVaultExchange.RestoreArchive(archive, output);
        Assert.True(Directory.Exists(output));
        Assert.Empty(Directory.EnumerateFileSystemEntries(output));
    }

    [Fact]
    public void SymlinkedFileIsNeverFollowed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string source = Folder("source");
        string outside = Path.Combine(root, "secret.txt");
        File.WriteAllText(outside, "secret");
        File.CreateSymbolicLink(Path.Combine(source, "secret.txt"), outside);
        Assert.Throws<InvalidDataException>(() => LocalVaultExchange.Capture(source));
    }

    [Fact]
    public void SymlinkedDirectoryIsNeverTraversed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string source = Folder("source");
        string outside = Folder("outside");
        Put(outside, "private.md", Encoding.UTF8.GetBytes("private"));
        Directory.CreateSymbolicLink(Path.Combine(source, "linked"), outside);
        Assert.Throws<InvalidDataException>(() => LocalVaultExchange.Capture(source));
    }

    [Fact]
    public void CaseFoldedDuplicatePathsCannotBeExported()
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            return;
        }

        string source = Folder("source");
        Put(source, "Notes/Draft.md", [1]);
        Put(source, "notes/draft.md", [2]);
        Assert.Throws<InvalidDataException>(() => LocalVaultExchange.Capture(source));
    }

    [Fact]
    public void NonPortableFilenameIsRejectedWhereFilesystemPermitsIt()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string source = Folder("source");
        Put(source, "bad?.md", Encoding.UTF8.GetBytes("content"));
        Assert.Throws<InvalidDataException>(() => LocalVaultExchange.Capture(source));
    }

    [Fact]
    public void RepeatedCapturePreservesFileContentButCreatesNewIndependentIdentities()
    {
        string source = Folder("source");
        Put(source, "Notes/one.md", Encoding.UTF8.GetBytes("body"));
        Put(source, "data.bin", [9, 9]);

        KnowledgeCollection first = LocalVaultExchange.Capture(source);
        KnowledgeCollection second = LocalVaultExchange.Capture(source);

        Assert.NotEqual(Assert.Single(first.Notes).Id, Assert.Single(second.Notes).Id);
        Assert.NotEqual(Assert.Single(first.Attachments).Id, Assert.Single(second.Attachments).Id);
        Assert.Equal(Assert.Single(first.Notes).Current.Markdown,
            Assert.Single(second.Notes).Current.Markdown);
        Assert.Equal(Assert.Single(first.Attachments).Sha256,
            Assert.Single(second.Attachments).Sha256);
    }

    [Fact]
    public void CaptureAndRestoreRetainsMarkdownBomAndLineEndings()
    {
        string source = Folder("source");
        byte[] bytes = new byte[] { 0xef, 0xbb, 0xbf, 35, 32, 84, 13, 10 };
        Put(source, "bom.md", bytes);
        string archive = Path.Combine(root, "bom.zip");
        LocalVaultExchange.CaptureArchive(source, archive);
        string output = Path.Combine(root, "restore");
        LocalVaultExchange.RestoreArchive(archive, output);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(output, "bom.md")));
    }

    private string Folder(string name)
    {
        string path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Put(string root, string path, byte[] contents)
    {
        string file = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, contents);
    }

    private static void AssertFilesEqual(string source, string restored)
    {
        string[] originals = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(source, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();
        string[] result = Directory.GetFiles(restored, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(restored, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(originals, result);
        foreach (string path in originals)
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(source, path)),
                File.ReadAllBytes(Path.Combine(restored, path)));
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
