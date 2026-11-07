using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Praxis.Knowledge;

public static partial class NoteArchive
{
    private const int MaxNotes = 10_000;
    private const int MaxRevisions = 50_000;
    private const int MaxNoteBytes = 8 * 1024 * 1024;
    private const int MaxManifestBytes = 8 * 1024 * 1024;
    private const long MaxTotalBytes = 256L * 1024 * 1024;
    private const long MaxArchiveFileBytes = 288L * 1024 * 1024;

    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public static void Write(Stream destination, IEnumerable<NoteRecord> records)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(records);
        WriteCore(destination, PrepareNotes(records), null);
    }

    public static IReadOnlyList<NoteRecord> Read(Stream source) => ReadCore(source, false).Notes;

    private static NoteRecord[] PrepareNotes(IEnumerable<NoteRecord> records)
    {
        var notes = records.Take(MaxNotes + 1).ToArray();
        ValidateNotes(notes);
        Array.Sort(notes, (left, right) => left.Id.CompareTo(right.Id));
        return notes;
    }

    private static List<ArchiveNote> DescribeNotes(NoteRecord[] notes, ref long totalBytes)
    {
        var result = new List<ArchiveNote>(notes.Length);
        foreach (var note in notes)
        {
            var entry = new ArchiveNote { Id = note.Id };
            foreach (var revision in note.Revisions)
            {
                var bytes = Utf8.GetBytes(revision.Markdown);
                if (bytes.Length > MaxNoteBytes || (totalBytes += bytes.Length) > MaxTotalBytes)
                {
                    throw new InvalidDataException("The note collection exceeds archive limits.");
                }

                entry.Revisions.Add(new ArchiveRevision
                {
                    Number = revision.Number,
                    Path = revision.Path,
                    Entry = NoteEntryName(note.Id, revision.Number),
                    Length = bytes.Length,
                    Sha256 = Hash(bytes)
                });
            }

            result.Add(entry);
        }

        return result;
    }

    private static void WriteNotes(ZipArchive archive, NoteRecord[] notes, IReadOnlyList<ArchiveNote> entries)
    {
        for (var noteIndex = 0; noteIndex < notes.Length; noteIndex++)
        {
            for (var index = 0; index < notes[noteIndex].Revisions.Count; index++)
            {
                var bytes = Utf8.GetBytes(notes[noteIndex].Revisions[index].Markdown);
                using var payload = archive.CreateEntry(entries[noteIndex].Revisions[index].Entry!,
                    CompressionLevel.Optimal).Open();
                payload.Write(bytes);
            }
        }
    }

    private static ArchiveManifest ReadManifest(
        ZipArchive archive, bool allowAttachments,
        out Dictionary<string, ZipArchiveEntry> entries, out long totalBytes)
    {
        if (archive.Entries.Count > MaxRevisions + (allowAttachments ? MaxAttachments : 0) + 1)
        {
            throw new InvalidDataException("The archive contains too many entries.");
        }

        entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (!entries.TryAdd(entry.FullName, entry))
            {
                throw new InvalidDataException("The archive contains duplicate entries.");
            }
        }

        if (!entries.TryGetValue("manifest.json", out var manifestEntry))
        {
            throw new InvalidDataException("The archive is missing its manifest.");
        }

        var manifestBytes = ReadEntry(manifestEntry, MaxManifestBytes);
        ArchiveManifest manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ArchiveManifest>(manifestBytes, JsonOptions)
                ?? throw new InvalidDataException("The archive manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The archive manifest is invalid.", exception);
        }

        if (manifest.FormatVersion != 1 && !(allowAttachments && manifest.FormatVersion == 2))
        {
            throw new NotSupportedException("The archive format version is not supported.");
        }

        totalBytes = manifestBytes.Length;
        return manifest;
    }

    private static List<NoteRecord> ReadNotes(
        ArchiveManifest manifest, IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        HashSet<string> consumed, ref long totalBytes)
    {
        if (manifest.Notes is null || manifest.Notes.Count > MaxNotes)
        {
            throw new InvalidDataException("The archive note list is invalid.");
        }

        var notes = new List<NoteRecord>(manifest.Notes.Count);
        var revisionCount = 0;
        foreach (var note in manifest.Notes)
        {
            if (note is null || note.Revisions is null || note.Revisions.Count == 0 ||
                (revisionCount += note.Revisions.Count) > MaxRevisions)
            {
                throw new InvalidDataException("The archive revision list is invalid.");
            }

            var revisions = new List<NoteRevision>(note.Revisions.Count);
            foreach (var revision in note.Revisions)
            {
                if (revision is null || revision.Path is null || revision.Entry is null ||
                    revision.Sha256 is null || revision.Length < 0 ||
                    revision.Length > MaxNoteBytes || revision.Number != revisions.Count + 1)
                {
                    throw new InvalidDataException("The archive contains an invalid revision.");
                }

                try
                {
                    PortablePath.Validate(revision.Path, markdown: true);
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException("The archive contains an invalid note path.", exception);
                }

                var expected = NoteEntryName(note.Id, revision.Number);
                if (!string.Equals(revision.Entry, expected, StringComparison.Ordinal) ||
                    !consumed.Add(expected) || !entries.TryGetValue(expected, out var payload))
                {
                    throw new InvalidDataException("The archive contains an invalid or missing note entry.");
                }

                if ((totalBytes += revision.Length) > MaxTotalBytes || payload.Length != revision.Length)
                {
                    throw new InvalidDataException("The archive exceeds size limits or contains an invalid length.");
                }

                var bytes = ReadEntry(payload, MaxNoteBytes);
                if (!string.Equals(Hash(bytes), revision.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("The archive content failed integrity verification.");
                }

                try
                {
                    revisions.Add(new NoteRevision(revision.Number, revision.Path, Utf8.GetString(bytes)));
                }
                catch (DecoderFallbackException exception)
                {
                    throw new InvalidDataException("The archive contains invalid UTF-8.", exception);
                }
            }

            try
            {
                notes.Add(NoteRecord.Restore(note.Id, revisions.ToArray()));
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("The archive contains an invalid note history.", exception);
            }
        }

        ValidateNotes(notes);
        return notes;
    }

    private static void ValidateNotes(IReadOnlyCollection<NoteRecord> notes)
    {
        if (notes.Count > MaxNotes)
        {
            throw new InvalidDataException("The collection contains too many notes.");
        }

        var ids = new HashSet<Guid>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var revisionCount = 0;
        foreach (var note in notes)
        {
            if (note is null || !ids.Add(note.Id) || !paths.Add(note.Current.Path) ||
                (revisionCount += note.Revisions.Count) > MaxRevisions)
            {
                throw new InvalidDataException("The collection contains duplicate identities, paths or excessive history.");
            }
        }
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry, int maxBytes)
    {
        if (entry.Length < 0 || entry.Length > maxBytes)
        {
            throw new InvalidDataException("The archive entry is too large.");
        }

        var bytes = new byte[(int)entry.Length];
        using var stream = entry.Open();
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("The archive entry size is inconsistent.");
        }

        return bytes;
    }

    private static string NoteEntryName(Guid id, int number) => $"notes/{id:N}/{number:D8}.md";

    private static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class ArchiveManifest
    {
        public int FormatVersion { get; set; }
        public List<ArchiveNote>? Notes { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ArchiveAttachment>? Attachments { get; set; }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<AttachmentReference>? References { get; set; }
    }

    private sealed class ArchiveNote
    {
        public Guid Id { get; set; }
        public List<ArchiveRevision> Revisions { get; set; } = [];
    }

    private sealed class ArchiveRevision
    {
        public int Number { get; set; }
        public string? Path { get; set; }
        public string? Entry { get; set; }
        public int Length { get; set; }
        public string? Sha256 { get; set; }
    }

    private sealed class ArchiveAttachment
    {
        public Guid Id { get; set; }
        public string? Path { get; set; }
        public string? Entry { get; set; }
        public int Length { get; set; }
        public string? Sha256 { get; set; }
    }
}
