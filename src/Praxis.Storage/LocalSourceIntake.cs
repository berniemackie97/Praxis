using System.Security.Cryptography;
using System.Text;

namespace Praxis.Storage;

public sealed record LocalImportedFile(string SourcePath, CapturedOriginal Original);

public sealed class LocalSourceIntake(LocalCatalog catalog)
{
    public async Task<CapturedOriginal> ImportFileAsync(
        string sourcePath, long maxBytes, string requestKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestKey);
        if (requestKey.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(requestKey));
        }

        if (maxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        var path = ValidateSourcePath(sourcePath, expectDirectory: false);
        cancellationToken.ThrowIfCancellationRequested();
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var accepted = await catalog.CaptureFileAsync(path, input, maxBytes, requestKey,
            cancellationToken);
        if (accepted.Length > maxBytes)
        {
            throw new InvalidDataException("The committed original exceeds the import byte limit.");
        }

        return accepted;
    }

    public async Task<IReadOnlyList<LocalImportedFile>> ImportFolderAsync(
        string selectedDirectory, string batchKey, int maxFiles, int maxDepth,
        long maxFileBytes, long maxTotalBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchKey);
        if (batchKey.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(batchKey));
        }

        if (maxFiles is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFiles));
        }

        if (maxDepth is < 0 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth));
        }

        if (maxFileBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        }

        if (maxTotalBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxTotalBytes));
        }

        var root = ValidateSourcePath(selectedDirectory, expectDirectory: true);
        var paths = DiscoverFiles(root, maxFiles, maxDepth, cancellationToken);
        var remaining = maxTotalBytes;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var length = new FileInfo(path).Length;
            if (length > maxFileBytes || length > remaining)
            {
                throw new InvalidDataException("The selected folder exceeds its import budget.");
            }

            remaining -= length;
        }

        var imported = new List<LocalImportedFile>(paths.Count);
        remaining = maxTotalBytes;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scopedKey = "folder-" + Convert.ToHexStringLower(SHA256.HashData(
                Encoding.UTF8.GetBytes($"praxis-folder-v1\n{batchKey}\n{path}")));
            var accepted = await ImportFileAsync(path, Math.Min(maxFileBytes, remaining),
                scopedKey, cancellationToken);
            remaining -= accepted.Length;
            imported.Add(new LocalImportedFile(path, accepted));
        }

        return imported;
    }

    private string ValidateSourcePath(string sourcePath, bool expectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!Path.IsPathFullyQualified(sourcePath))
        {
            throw new ArgumentException("A selected source path must be absolute.", nameof(sourcePath));
        }

        var full = Path.GetFullPath(sourcePath);
        var dataRoot = Path.GetDirectoryName(catalog.DatabasePath)!;
        var relative = Path.GetRelativePath(dataRoot, full);
        if (relative == "." ||
            (!Path.IsPathRooted(relative) && relative != ".." &&
             !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
             !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("The data store cannot be imported as a source.");
        }

        var attributes = File.GetAttributes(full);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("A selected source path cannot be a symbolic link.");
        }

        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        if (isDirectory != expectDirectory)
        {
            throw new InvalidDataException("The selected source has an unexpected file type.");
        }

        return full;
    }

    private List<string> DiscoverFiles(
        string directory, int maxFiles, int maxDepth,
        CancellationToken cancellationToken)
    {
        var result = new List<string>();
        var stack = new Stack<(string Directory, int Depth)>();
        stack.Push((directory, 0));
        var remainingEntries = checked(maxFiles * 4);
        while (stack.TryPop(out var pending))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = new List<string>();
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (--remainingEntries < 0)
                {
                    throw new InvalidDataException("The folder contains too many entries.");
                }

                entries.Add(entry);
            }

            entries.Sort(StringComparer.Ordinal);
            for (var index = entries.Count - 1; index >= 0; index--)
            {
                var path = entries[index];
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Folder imports cannot follow symbolic links.");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (pending.Depth == maxDepth)
                    {
                        throw new InvalidDataException("The folder nesting depth exceeds the limit.");
                    }

                    stack.Push((ValidateSourcePath(path, expectDirectory: true),
                        pending.Depth + 1));
                    continue;
                }

                if (result.Count >= maxFiles)
                {
                    throw new InvalidDataException("The folder contains too many files.");
                }

                result.Add(ValidateSourcePath(path, expectDirectory: false));
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }
}
