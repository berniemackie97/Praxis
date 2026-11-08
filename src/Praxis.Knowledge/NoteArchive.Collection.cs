using System.IO.Compression;
using System.Text.Json;

namespace Praxis.Knowledge;

public static partial class NoteArchive
{
    private const int MaxAttachments = 10_000;
    private const int MaxAttachmentBytes = 32 * 1024 * 1024;
    private const int MaxReferences = 50_000;

    public static void WriteCollection(Stream destination, KnowledgeCollection collection)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(collection);
        WriteCore(destination, PrepareNotes(collection.Notes), collection);
    }

    public static KnowledgeCollection ReadCollection(Stream source) => ReadCore(source, true);

    private static void WriteCore(Stream destination, NoteRecord[] notes, KnowledgeCollection? collection)
    {
        long totalBytes = 0;
        var manifest = new ArchiveManifest
        {
            FormatVersion = collection is null ? 1 : 2,
            Notes = DescribeNotes(notes, ref totalBytes)
        };

        var payloads = new SortedDictionary<string, AttachmentRecord>(StringComparer.Ordinal);
        if (collection is not null)
        {
            if (collection.Attachments.Count > MaxAttachments || collection.References.Count > MaxReferences)
            {
                throw new InvalidDataException("The collection contains too many attachments or references.");
            }

            manifest.Attachments = [];
            foreach (var attachment in collection.Attachments.OrderBy(item => item.Id))
            {
                if (attachment.Length > MaxAttachmentBytes)
                {
                    throw new InvalidDataException("An attachment exceeds the archive size limit.");
                }

                var blobName = $"attachments/{attachment.Sha256}";
                if (payloads.TryAdd(blobName, attachment) &&
                    (totalBytes += attachment.Length) > MaxTotalBytes)
                {
                    throw new InvalidDataException("The collection exceeds archive size limits.");
                }

                manifest.Attachments.Add(new ArchiveAttachment
                {
                    Id = attachment.Id,
                    Path = attachment.Path,
                    Entry = blobName,
                    Length = attachment.Length,
                    Sha256 = attachment.Sha256
                });
            }

            manifest.References = collection.References
                .OrderBy(reference => reference.NoteId)
                .ThenBy(reference => reference.AttachmentId)
                .ToList();
        }

        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        if (manifestBytes.Length > MaxManifestBytes || totalBytes + manifestBytes.Length > MaxTotalBytes)
        {
            throw new InvalidDataException("The archive manifest exceeds size limits.");
        }

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        WriteNotes(archive, notes, manifest.Notes);
        foreach (var (name, attachment) in payloads)
        {
            using var payload = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
            payload.Write(attachment.ContentSpan);
        }

        using var manifestStream = archive.CreateEntry("manifest.json", CompressionLevel.Optimal).Open();
        manifestStream.Write(manifestBytes);
    }

    private static KnowledgeCollection ReadCore(Stream source, bool acceptAttachments)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead || !source.CanSeek)
        {
            throw new ArgumentException("Archive input must be readable and seekable.", nameof(source));
        }

        if (source.Length > MaxArchiveFileBytes)
        {
            throw new InvalidDataException("The archive file exceeds size limits.");
        }

        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        var manifest = ReadManifest(archive, acceptAttachments, out var entries, out var totalBytes);

        if (manifest.FormatVersion == 1 &&
            (manifest.Attachments is not null || manifest.References is not null))
        {
            throw new InvalidDataException("The version one manifest has unexpected attachment metadata.");
        }

        var consumed = new HashSet<string>(StringComparer.Ordinal) { "manifest.json" };
        var notes = ReadNotes(manifest, entries, consumed, ref totalBytes);
        var attachments = new List<AttachmentRecord>();
        var references = new List<AttachmentReference>();
        if (manifest.FormatVersion == 2)
        {
            if (manifest.Attachments is null || manifest.References is null ||
                manifest.Attachments.Count > MaxAttachments ||
                manifest.References.Count > MaxReferences)
            {
                throw new InvalidDataException("The attachment metadata is invalid.");
            }

            attachments = ReadAttachments(manifest.Attachments, entries, consumed, ref totalBytes);
            references = manifest.References;
        }

        if (consumed.Count != entries.Count)
        {
            throw new InvalidDataException("The archive contains unreferenced entries.");
        }

        try
        {
            return new KnowledgeCollection(notes, attachments, references);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException("The archive contains invalid identities, paths, or references.", exception);
        }
    }

    private static List<AttachmentRecord> ReadAttachments(
        IEnumerable<ArchiveAttachment> metadata, IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        HashSet<string> consumed, ref long totalBytes)
    {
        var attachments = new List<AttachmentRecord>();
        var blobs = new Dictionary<string, byte[]>(StringComparer.Ordinal);

        foreach (var item in metadata)
        {
            if (item is null || item.Id == Guid.Empty ||
                item.Path is null || item.Entry is null || item.Sha256 is null ||
                item.Length < 0 || item.Length > MaxAttachmentBytes)
            {
                throw new InvalidDataException("The archive contains invalid attachment metadata.");
            }

            try
            {
                PortablePath.Validate(item.Path);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("The attachment path is invalid.", exception);
            }

            var expectedEntry = $"attachments/{item.Sha256}";
            if (!string.Equals(item.Entry, expectedEntry, StringComparison.Ordinal) ||
                !entries.TryGetValue(expectedEntry, out var payload) ||
                payload.Length != item.Length)
            {
                throw new InvalidDataException("An attachment is missing or has an invalid size.");
            }

            if (!blobs.TryGetValue(expectedEntry, out var bytes))
            {
                if ((totalBytes += item.Length) > MaxTotalBytes)
                {
                    throw new InvalidDataException("The archive exceeds size limits.");
                }

                bytes = ReadEntry(payload, MaxAttachmentBytes);
                if (!string.Equals(Hash(bytes), item.Sha256, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("An attachment failed integrity verification.");
                }

                blobs.Add(expectedEntry, bytes);
                consumed.Add(expectedEntry);
            }

            attachments.Add(AttachmentRecord.Restore(item.Id, item.Path, bytes));
        }

        return attachments;
    }
}
