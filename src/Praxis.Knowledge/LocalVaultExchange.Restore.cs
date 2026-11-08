using System.Security.Cryptography;

namespace Praxis.Knowledge;

public static partial class LocalVaultExchange
{
    public static void CaptureArchive(string vaultDirectory, string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var source = Path.GetFullPath(vaultDirectory);
        var destination = Path.GetFullPath(archivePath);
        if (IsWithin(source, destination))
        {
            throw new ArgumentException("The archive output must be outside the source vault.", nameof(archivePath));
        }

        WriteArchive(Capture(source), destination);
    }

    public static VaultUpdate PlanUpdate(
        string vaultDirectory, string previousArchivePath,
        IReadOnlyList<VaultRename>? renames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousArchivePath);
        var source = Path.GetFullPath(vaultDirectory);
        var previousPath = Path.GetFullPath(previousArchivePath);
        if (IsWithin(source, previousPath))
        {
            throw new ArgumentException("The prior archive must be outside the source vault.", nameof(previousArchivePath));
        }

        using var input = new FileStream(previousPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var previous = NoteArchive.ReadCollection(input);
        return LocalVaultReconciler.Reconcile(previous, Capture(source), renames);
    }

    public static VaultUpdate UpdateArchive(
        string vaultDirectory, string previousArchivePath, string newArchivePath,
        IReadOnlyList<VaultRename>? renames = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(newArchivePath);
        var destination = Path.GetFullPath(newArchivePath);
        if (IsWithin(Path.GetFullPath(vaultDirectory), destination))
        {
            throw new ArgumentException("The new archive must be outside the source vault.", nameof(newArchivePath));
        }

        var update = PlanUpdate(vaultDirectory, previousArchivePath, renames);
        WriteArchive(update.Collection, destination);
        return update;
    }

    private static void WriteArchive(KnowledgeCollection collection, string destination)
    {
        var parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent) || Path.Exists(destination))
        {
            throw new IOException("The archive parent must exist and the destination must not exist.");
        }

        CheckLink(parent);
        var temporary = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.pending");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                NoteArchive.WriteCollection(output, collection);
                output.Flush(flushToDisk: true);
            }

            File.Move(temporary, destination);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    public static void RestoreArchive(string archivePath, string destinationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        RestoreCollection(NoteArchive.ReadCollection(stream), destinationDirectory);
    }

    public static void RestoreCollection(KnowledgeCollection collection, string destinationDirectory)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var destination = Path.GetFullPath(destinationDirectory);
        var parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent) || Path.Exists(destination))
        {
            throw new IOException("Restore requires an existing parent and a new destination directory.");
        }

        CheckLink(parent);
        var planned = Prepare(collection);
        var temporary = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.restore");
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var (path, bytes) in planned)
            {
                var target = Path.Combine(temporary, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var stream = new FileStream(target, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            Directory.Move(temporary, destination);
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                Directory.Delete(temporary, recursive: true);
            }
        }
    }

    private static IReadOnlyList<(string Path, byte[] Bytes)> Prepare(KnowledgeCollection collection)
    {
        if (collection.Notes.Count > MaxNotes || collection.Attachments.Count > MaxAttachments)
        {
            throw new InvalidDataException("The collection contains too many vault files.");
        }

        var result = new List<(string Path, byte[] Bytes)>(
            collection.Notes.Count + collection.Attachments.Count);
        var allPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;

        foreach (var note in collection.Notes)
        {
            var bytes = Utf8.GetBytes(note.Current.Markdown);
            Add(note.Current.Path, bytes, MaxNoteBytes);
        }

        foreach (var attachment in collection.Attachments)
        {
            var bytes = attachment.ReadContent();
            if (!string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(bytes)),
                attachment.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The original attachment failed integrity verification.");
            }

            Add(attachment.Path, bytes, MaxAttachmentBytes);
        }

        var ordered = result.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
        var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, _) in ordered)
        {
            var index = path.IndexOf('/');
            while (index >= 0)
            {
                var parent = path[..index];
                if (allPaths.Contains(parent) ||
                    directories.TryGetValue(parent, out var existing) && existing != parent)
                {
                    throw new InvalidDataException("The collection contains conflicting directory paths.");
                }

                directories.TryAdd(parent, parent);
                index = path.IndexOf('/', index + 1);
            }
        }

        return ordered;

        void Add(string path, byte[] bytes, int maximum)
        {
            PortablePath.Validate(path, markdown: maximum == MaxNoteBytes);
            if (!allPaths.Add(path) || bytes.Length > maximum ||
                (totalBytes += bytes.Length) > MaxTotalBytes)
            {
                throw new InvalidDataException("The collection contains conflicting paths or excessive data.");
            }

            result.Add((path, bytes));
        }
    }

    private static bool IsWithin(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "." ||
            relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }
}
