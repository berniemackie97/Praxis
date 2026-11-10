using System.Security.Cryptography;
using Praxis.Knowledge.Archives;
using Praxis.Knowledge.Model;

namespace Praxis.Knowledge.Vaults;

public static partial class LocalVaultExchange
{
    public static void CaptureArchive(string vaultDirectory, string archivePath, bool trackLinks = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        string source = Path.GetFullPath(vaultDirectory);
        string destination = Path.GetFullPath(archivePath);
        if (IsWithin(source, destination))
        {
            throw new ArgumentException("The archive output must be outside the source vault.", nameof(archivePath));
        }

        KnowledgeCollection collection = Capture(source);
        WriteArchive(trackLinks ? LocalVaultAttachmentLinks.Track(collection) : collection, destination);
    }

    public static VaultUpdate PlanUpdate(string vaultDirectory, string previousArchivePath, IReadOnlyList<VaultRename>? renames = null, bool trackLinks = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(previousArchivePath);
        string source = Path.GetFullPath(vaultDirectory);
        string previousPath = Path.GetFullPath(previousArchivePath);
        if (IsWithin(source, previousPath))
        {
            throw new ArgumentException("The prior archive must be outside the source vault.", nameof(previousArchivePath));
        }

        using FileStream input = new(previousPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        KnowledgeCollection previous = NoteArchive.ReadCollection(input);
        return LocalVaultReconciler.Reconcile(previous, Capture(source), renames, trackLinks, previewLinkRemovals: true);
    }

    public static VaultUpdate UpdateArchive(string vaultDirectory, string previousArchivePath, string newArchivePath, IReadOnlyList<VaultRename>? renames = null,
        bool trackLinks = false, IReadOnlyList<AttachmentReference>? approvedLinkRemovals = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(newArchivePath);
        string destination = Path.GetFullPath(newArchivePath);
        if (IsWithin(Path.GetFullPath(vaultDirectory), destination))
        {
            throw new ArgumentException("The new archive must be outside the source vault.", nameof(newArchivePath));
        }

        VaultUpdate update = PlanUpdate(vaultDirectory, previousArchivePath, renames, trackLinks);
        IReadOnlyList<AttachmentReference> approved = approvedLinkRemovals ?? [];
        HashSet<AttachmentReference> distinct = approved.ToHashSet();
        if (distinct.Count != approved.Count || distinct.Count != update.RemovedLinks || !distinct.SetEquals(update.RemovedLinkPairs ?? []))
        {
            throw new InvalidOperationException("Approval must list exactly the disappearing vault attachment reference pairs.");
        }

        WriteArchive(update.Collection, destination);
        return update;
    }

    private static void WriteArchive(KnowledgeCollection collection, string destination)
    {
        string parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent) || Path.Exists(destination))
        {
            throw new IOException("The archive parent must exist and the destination must not exist.");
        }

        CheckLink(parent);
        string temporary = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.pending");
        try
        {
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
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
        using FileStream stream = new(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        RestoreCollection(NoteArchive.ReadCollection(stream), destinationDirectory);
    }

    public static void RestoreCollection(KnowledgeCollection collection, string destinationDirectory)
    {
        ArgumentNullException.ThrowIfNull(collection);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        string destination = Path.GetFullPath(destinationDirectory);
        string parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent) || Path.Exists(destination))
        {
            throw new IOException("Restore requires an existing parent and a new destination directory.");
        }

        CheckLink(parent);
        IReadOnlyList<(string Path, byte[] Bytes)> planned = Prepare(collection);
        string temporary = Path.Combine(parent, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.restore");
        Directory.CreateDirectory(temporary);
        try
        {
            foreach ((string path, byte[] bytes) in planned)
            {
                string target = Path.Combine(temporary, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using FileStream stream = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
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

        List<(string Path, byte[] Bytes)> result = new(collection.Notes.Count + collection.Attachments.Count);
        HashSet<string> allPaths = new(StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;

        foreach (NoteRecord note in collection.Notes)
        {
            byte[] bytes = Utf8.GetBytes(note.Current.Markdown);
            Add(note.Current.Path, bytes, MaxNoteBytes);
        }

        foreach (AttachmentRecord attachment in collection.Attachments)
        {
            byte[] bytes = attachment.ReadContent();
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(bytes)), attachment.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The original attachment failed integrity verification.");
            }

            Add(attachment.Path, bytes, MaxAttachmentBytes);
        }

        (string Path, byte[] Bytes)[] ordered = result.OrderBy(item => item.Path, StringComparer.Ordinal).ToArray();
        Dictionary<string, string> directories = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, byte[] _) in ordered)
        {
            int index = path.IndexOf('/');
            while (index >= 0)
            {
                string parent = path[..index];
                if (allPaths.Contains(parent) || directories.TryGetValue(parent, out string? existing) && existing != parent)
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
            if (!allPaths.Add(path) || bytes.Length > maximum || (totalBytes += bytes.Length) > MaxTotalBytes)
            {
                throw new InvalidDataException("The collection contains conflicting paths or excessive data.");
            }

            result.Add((path, bytes));
        }
    }

    private static bool IsWithin(string root, string candidate)
    {
        string relative = Path.GetRelativePath(root, candidate);
        return relative == "." || relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.IsPathRooted(relative);
    }
}
