using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Praxis.Notes.Archives;
using Praxis.Notes.Model;
using Xunit;

namespace Praxis.Notes.Tests.Archives;

public sealed class NoteArchiveTests
{
    [Fact]
    public void ArchivePreservesIdentityPathsHistoryAndMarkdown()
    {
        NoteRecord first = NoteRecord.Create("Writing/ideas.md", "# Café\r\n\\* literal\n")
            .Revise("Archive/ideas.md", "# Café\r\nFinal\n");
        NoteRecord second = NoteRecord.Create("notes/日本語.md", "# 日本語\n");

        using MemoryStream archive = Export(second, first);
        IReadOnlyList<NoteRecord> restored = NoteArchive.Read(archive);

        Assert.Equal(2, restored.Count);
        NoteRecord firstCopy = Assert.Single(restored, note => note.Id == first.Id);
        NoteRecord secondCopy = Assert.Single(restored, note => note.Id == second.Id);
        Assert.Equal(first.Revisions.ToArray(), firstCopy.Revisions.ToArray());
        Assert.Equal(second.Revisions.ToArray(), secondCopy.Revisions.ToArray());
    }

    [Fact]
    public void EmptyCollectionRoundTrips()
    {
        using MemoryStream archive = Export();

        Assert.Empty(NoteArchive.Read(archive));
    }

    [Fact]
    public void ExportDoesNotCloseCallerStream()
    {
        using MemoryStream archive = Export(NoteRecord.Create("note.md", "# Note"));

        Assert.True(archive.CanRead);
        Assert.NotEmpty(NoteArchive.Read(archive));
        Assert.True(archive.CanRead);
    }

    [Fact]
    public void DuplicateCurrentPathsAreRejectedCaseInsensitively()
    {
        NoteRecord a = NoteRecord.Create("Notes/Entry.md", "a");
        NoteRecord b = NoteRecord.Create("notes/entry.md", "b");

        Assert.Throws<InvalidDataException>(() => Export(a, b));
    }

    [Fact]
    public void DuplicateIdsAreRejected()
    {
        NoteRecord note = NoteRecord.Create("a.md", "a");

        Assert.Throws<InvalidDataException>(() => Export(note, note));
    }

    [Fact]
    public void AlteredContentIsRejected()
    {
        NoteRecord note = NoteRecord.Create("note.md", "original");
        using MemoryStream archive = Export(note);
        ArchiveFixture.ReplaceEntry(archive, $"notes/{note.Id:N}/00000001.md", _ => Encoding.UTF8.GetBytes("altered!"));

        Assert.Throws<InvalidDataException>(() => NoteArchive.Read(archive));
    }

    [Fact]
    public void UnknownFormatVersionIsRejected()
    {
        using MemoryStream archive = Export(NoteRecord.Create("note.md", "content"));
        ArchiveFixture.ReplaceEntry(archive, "manifest.json", bytes =>
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(
                "\"formatVersion\": 1", "\"formatVersion\": 2", StringComparison.Ordinal)));

        Assert.Throws<NotSupportedException>(() => NoteArchive.Read(archive));
    }

    [Fact]
    public void InvalidManifestPathIsRejected()
    {
        using MemoryStream archive = Export(NoteRecord.Create("safe.md", "content"));
        ArchiveFixture.ReplaceEntry(archive, "manifest.json", bytes =>
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace(
                "\"path\": \"safe.md\"", "\"path\": \"../escape.md\"", StringComparison.Ordinal)));

        Assert.Throws<InvalidDataException>(() => NoteArchive.Read(archive));
    }

    [Fact]
    public void UnexpectedFilesAreRejected()
    {
        using MemoryStream archive = Export(NoteRecord.Create("safe.md", "content"));
        using (ZipArchive zip = new(archive, ZipArchiveMode.Update, leaveOpen: true))
        {
            using Stream extra = zip.CreateEntry("../outside.md").Open();
            extra.WriteByte(42);
        }

        archive.Position = 0;
        Assert.Throws<InvalidDataException>(() => NoteArchive.Read(archive));
    }

    [Fact]
    public void MissingManifestIsRejected()
    {
        using MemoryStream archive = new();
        using (ZipArchive zip = new(archive, ZipArchiveMode.Create, leaveOpen: true))
        {
            using Stream entry = zip.CreateEntry("file.md").Open();
            entry.WriteByte(42);
        }

        archive.Position = 0;
        Assert.Throws<InvalidDataException>(() => NoteArchive.Read(archive));
    }

    [Fact]
    public void CorruptedManifestIsRejected()
    {
        using MemoryStream archive = Export(NoteRecord.Create("note.md", "content"));
        ArchiveFixture.ReplaceEntry(archive, "manifest.json", _ => Encoding.UTF8.GetBytes("{"));

        Assert.Throws<InvalidDataException>(() => NoteArchive.Read(archive));
    }

    [Fact]
    public void ReadsIndependentVersionOneArchive()
    {
        Guid id = Guid.NewGuid();
        using MemoryStream archive = IndependentArchive(id, "Examples/fixture.md", Encoding.UTF8.GetBytes("# Fixture\r\n"));

        NoteRecord note = Assert.Single(NoteArchive.Read(archive));
        Assert.Equal(id, note.Id);
        Assert.Equal("Examples/fixture.md", note.Current.Path);
        Assert.Equal("# Fixture\r\n", note.Current.Markdown);
    }

    [Fact]
    public void InvalidUtf8IsRejectedEvenWhenHashMatches()
    {
        using MemoryStream archive = IndependentArchive(Guid.NewGuid(), "bad.md", [0xff]);

        Assert.Throws<InvalidDataException>(() => NoteArchive.Read(archive));
    }

    [Fact]
    public void OversizedNoteIsRejectedBeforeWriting()
    {
        NoteRecord note = NoteRecord.Create("large.md", new string('x', 8 * 1024 * 1024 + 1));
        using MemoryStream archive = new();

        Assert.Throws<InvalidDataException>(() => NoteArchive.Write(archive, [note]));
        Assert.Equal(0, archive.Length);
    }

    [Fact]
    public void ExcessiveRecordInputIsRejectedWithoutEnumeratingItAll()
    {
        NoteRecord note = NoteRecord.Create("note.md", "content");
        using MemoryStream archive = new();

        Assert.Throws<InvalidDataException>(() =>
            NoteArchive.Write(archive, Enumerable.Repeat(note, int.MaxValue)));
        Assert.Equal(0, archive.Length);
    }

    private static MemoryStream IndependentArchive(Guid id, string path, byte[] content)
    {
        string entryName = $"notes/{id:N}/00000001.md";
        string hash = Convert.ToHexStringLower(SHA256.HashData(content));
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            formatVersion = 1,
            notes = new[]
            {
                new
                {
                    id,
                    revisions = new[]
                    {
                        new { number = 1, path, entry = entryName, length = content.Length, sha256 = hash }
                    }
                }
            }
        });

        MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (Stream payload = zip.CreateEntry(entryName).Open())
            {
                payload.Write(content);
            }

            using Stream index = zip.CreateEntry("manifest.json").Open();
            index.Write(manifest);
        }

        stream.Position = 0;
        return stream;
    }

    private static MemoryStream Export(params NoteRecord[] records)
    {
        MemoryStream stream = new();
        NoteArchive.Write(stream, records);
        stream.Position = 0;
        return stream;
    }

}
