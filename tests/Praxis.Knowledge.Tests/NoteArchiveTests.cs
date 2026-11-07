using System.IO.Compression;
using System.Text;
using Praxis.Knowledge;
using Xunit;

namespace Praxis.Knowledge.Tests;

public sealed class NoteArchiveTests
{
    [Fact]
    public void ArchivePreservesIdentityPathsHistoryAndMarkdown()
    {
        var first = NoteRecord.Create("Writing/ideas.md", "# Café\r\n\\* literal\n")
            .Revise("Archive/ideas.md", "# Café\r\nFinal\n");
        var second = NoteRecord.Create("notes/日本語.md", "# 日本語\n");

        using var archive = Export(second, first);
        var restored = NoteArchive.Read(archive);

        Assert.Equal(2, restored.Count);
        var firstCopy = Assert.Single(restored, note => note.Id == first.Id);
        var secondCopy = Assert.Single(restored, note => note.Id == second.Id);
        Assert.Equal(first.Revisions.ToArray(), firstCopy.Revisions.ToArray());
        Assert.Equal(second.Revisions.ToArray(), secondCopy.Revisions.ToArray());
    }

    [Fact]
    public void EmptyCollectionRoundTrips()
    {
        using var archive = Export();

        Assert.Empty(NoteArchive.Read(archive));
    }

    [Fact]
    public void ExportDoesNotCloseCallerStream()
    {
        using var archive = Export(NoteRecord.Create("note.md", "# Note"));

        Assert.True(archive.CanRead);
        Assert.NotEmpty(NoteArchive.Read(archive));
        Assert.True(archive.CanRead);
    }

    [Fact]
    public void DuplicateCurrentPathsAreRejectedCaseInsensitively()
    {
        var a = NoteRecord.Create("Notes/Entry.md", "a");
        var b = NoteRecord.Create("notes/entry.md", "b");

        Assert.Throws<InvalidDataException>(() => Export(a, b));
    }

    [Fact]
    public void DuplicateIdsAreRejected()
    {
        var note = NoteRecord.Create("a.md", "a");

        Assert.Throws<InvalidDataException>(() => Export(note, note));
    }

    [Fact]
    public void AlteredContentIsRejected()
    {
        var note = NoteRecord.Create("note.md", "original");
        using var archive = Export(note);
        ReplaceEntry(archive, $"notes/{note.Id:N}/00000001.md", _ => Encoding.UTF8.GetBytes("altered!"));

        Assert.Throws<InvalidDataException>(() => NoteArchive.Read(archive));
    }

    [Fact]
    public void UnknownFormatVersionIsRejected()
    {
        using var archive = Export(NoteRecord.Create("note.md", "content"));
        ReplaceEntry(archive, "manifest.json", bytes =>
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(
                "\"formatVersion\": 1", "\"formatVersion\": 2", StringComparison.Ordinal)));

        Assert.Throws<NotSupportedException>(() => NoteArchive.Read(archive));
    }

    [Fact]
    public void InvalidManifestPathIsRejected()
    {
        using var archive = Export(NoteRecord.Create("safe.md", "content"));
        ReplaceEntry(archive, "manifest.json", bytes =>
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(
                "\"path\": \"safe.md\"", "\"path\": \"../escape.md\"", StringComparison.Ordinal)));

        Assert.Throws<InvalidDataException>(() => NoteArchive.Read(archive));
    }

    [Fact]
    public void UnexpectedFilesAreRejected()
    {
        using var archive = Export(NoteRecord.Create("safe.md", "content"));
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Update, leaveOpen: true))
        {
            using var extra = zip.CreateEntry("../outside.md").Open();
            extra.WriteByte(42);
        }

        archive.Position = 0;
        Assert.Throws<InvalidDataException>(() => NoteArchive.Read(archive));
    }

    [Fact]
    public void MissingManifestIsRejected()
    {
        using var archive = new MemoryStream();
        using (var zip = new ZipArchive(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("file.md").Open();
            entry.WriteByte(42);
        }

        archive.Position = 0;
        Assert.Throws<InvalidDataException>(() => NoteArchive.Read(archive));
    }

    [Fact]
    public void CorruptedManifestIsRejected()
    {
        using var archive = Export(NoteRecord.Create("note.md", "content"));
        ReplaceEntry(archive, "manifest.json", _ => Encoding.UTF8.GetBytes("{"));

        Assert.Throws<InvalidDataException>(() => NoteArchive.Read(archive));
    }

    private static MemoryStream Export(params NoteRecord[] records)
    {
        var stream = new MemoryStream();
        NoteArchive.Write(stream, records);
        stream.Position = 0;
        return stream;
    }

    private static void ReplaceEntry(MemoryStream stream, string name, Func<byte[], byte[]> replace)
    {
        stream.Position = 0;
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry(name) ?? throw new InvalidOperationException("Missing test entry.");
            byte[] original;
            using (var payload = entry.Open())
            using (var buffer = new MemoryStream())
            {
                payload.CopyTo(buffer);
                original = buffer.ToArray();
            }

            entry.Delete();
            using var updated = zip.CreateEntry(name).Open();
            updated.Write(replace(original));
        }

        stream.Position = 0;
    }
}
