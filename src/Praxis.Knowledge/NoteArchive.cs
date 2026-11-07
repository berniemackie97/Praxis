using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Praxis.Knowledge;

public static class NoteArchive
{
    private const int FormatVersion = 1;
    private const int MaxNotes = 10_000;
    private const int MaxRevisions = 50_000;
    private const int MaxNoteBytes = 8 * 1024 * 1024;
    private const int MaxManifestBytes = 8 * 1024 * 1024;
    private const long MaxTotalBytes = 256L * 1024 * 1024;

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

        var notes = records.ToArray();
        ValidateCollection(notes);
        Array.Sort(notes, (left, right) => left.Id.CompareTo(right.Id));

        long totalBytes = 0;
        foreach (var note in notes)
        {
            foreach (var revision in note.Revisions)
            {
                var length = Utf8.GetByteCount(revision.Markdown);
                if (length > MaxNoteBytes || (totalBytes += length) > MaxTotalBytes)
                {
                    throw new InvalidDataException("The note collection exceeds archive limits.");
                }
            }
        }

        var manifest = new ArchiveManifest { FormatVersion = FormatVersion };
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        foreach (var note in notes)
        {
            var noteManifest = new ArchiveNote { Id = note.Id };
            foreach (var revision in note.Revisions)
            {
                var entryName = EntryName(note.Id, revision.Number);
                var bytes = Utf8.GetBytes(revision.Markdown);
                using (var payload = archive.CreateEntry(entryName, CompressionLevel.Optimal).Open())
                {
                    payload.Write(bytes);
                }

                noteManifest.Revisions.Add(new ArchiveRevision
                {
                    Number = revision.Number,
                    Path = revision.Path,
                    Entry = entryName,
                    Length = bytes.Length,
                    Sha256 = Hash(bytes)
                });
            }

            manifest.Notes.Add(noteManifest);
        }

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (manifestBytes.Length > MaxManifestBytes || totalBytes + manifestBytes.Length > MaxTotalBytes)
        {
            throw new InvalidDataException("The archive manifest exceeds size limits.");
        }

        using var manifestStream = archive.CreateEntry("manifest.json", CompressionLevel.Optimal).Open();
        manifestStream.Write(manifestBytes);
    }

    public static IReadOnlyList<NoteRecord> Read(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);

        if (archive.Entries.Count > MaxRevisions + 1)
        {
            throw new InvalidDataException("The archive contains too many entries.");
        }

        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
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

        if (manifest.FormatVersion != FormatVersion)
        {
            throw new NotSupportedException("The archive format version is not supported.");
        }

        if (manifest.Notes is null || manifest.Notes.Count > MaxNotes)
        {
            throw new InvalidDataException("The archive note list is invalid.");
        }

        var consumed = new HashSet<string>(StringComparer.Ordinal) { "manifest.json" };
        var notes = new List<NoteRecord>(manifest.Notes.Count);
        long totalBytes = manifestBytes.Length;
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
                    NotePath.Validate(revision.Path);
                }
                catch (ArgumentException exception)
                {
                    throw new InvalidDataException("The archive contains an invalid note path.", exception);
                }

                var expectedName = EntryName(note.Id, revision.Number);
                if (!string.Equals(revision.Entry, expectedName, StringComparison.Ordinal) ||
                    !consumed.Add(revision.Entry) ||
                    !entries.TryGetValue(revision.Entry, out var payload))
                {
                    throw new InvalidDataException("The archive contains an invalid or missing content entry.");
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

        if (consumed.Count != entries.Count)
        {
            throw new InvalidDataException("The archive contains unreferenced entries.");
        }

        ValidateCollection(notes);
        return notes;
    }

    private static void ValidateCollection(IReadOnlyCollection<NoteRecord> notes)
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

    private static string EntryName(Guid id, int number) => $"notes/{id:N}/{number:D8}.md";

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class ArchiveManifest
    {
        public int FormatVersion { get; set; }
        public List<ArchiveNote> Notes { get; set; } = [];
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
}
