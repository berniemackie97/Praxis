using System.Security.Cryptography;
using System.Text;
using Praxis.Storage.Catalog;
using Praxis.Storage.Originals;

namespace Praxis.Storage.Intake;

public sealed record LocalImportedFile(string SourcePath, CapturedOriginal Original);
public sealed record LocalObservedFile(string SourcePath, long Length, DateTime LastWriteUtc);

public sealed class LocalSourceIntake(LocalCatalog catalog)
{
    public IReadOnlyList<LocalObservedFile> InspectFolder(string selectedDirectory, int maxFiles, int maxDepth, long maxTotalBytes, CancellationToken cancellationToken = default)
    {
        if (maxFiles is < 1 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFiles));
        }

        if (maxDepth is < 0 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(maxDepth));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(maxTotalBytes);

        string root = ValidateSourcePath(selectedDirectory, expectDirectory: true);
        List<string> paths = DiscoverFiles(root, maxFiles, maxDepth, cancellationToken);
        List<LocalObservedFile> files = new(paths.Count);
        long remaining = maxTotalBytes;
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info = new(path);
            long length = info.Length;
            if (length > remaining)
            {
                throw new InvalidDataException("The selected folder exceeds its inspection byte budget.");
            }

            remaining -= length;
            files.Add(new LocalObservedFile(path, length, info.LastWriteTimeUtc));
        }

        return files;
    }

    public async Task<CapturedOriginal> ImportFileAsync(string sourcePath, long maxBytes, string requestKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestKey);
        if (requestKey.Length > 128)
        {
            throw new ArgumentOutOfRangeException(nameof(requestKey));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);

        string path = ValidateSourcePath(sourcePath, expectDirectory: false);
        cancellationToken.ThrowIfCancellationRequested();
        await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        CapturedOriginal accepted = await catalog.Originals.CaptureFileAsync(path, input, maxBytes, requestKey, cancellationToken);
        return accepted.Length > maxBytes
            ? throw new InvalidDataException("The committed original exceeds the import byte limit.")
            : accepted;
    }

    public async Task<IReadOnlyList<LocalImportedFile>> ImportFolderAsync(string selectedDirectory, string batchKey, int maxFiles, int maxDepth, long maxFileBytes, long maxTotalBytes, CancellationToken cancellationToken = default)
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

        ArgumentOutOfRangeException.ThrowIfNegative(maxFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxTotalBytes);

        string root = ValidateSourcePath(selectedDirectory, expectDirectory: true);
        List<string> paths = DiscoverFiles(root, maxFiles, maxDepth, cancellationToken);
        long remaining = maxTotalBytes;
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long length = new FileInfo(path).Length;
            if (length > maxFileBytes || length > remaining)
            {
                throw new InvalidDataException("The selected folder exceeds its import budget.");
            }

            remaining -= length;
        }

        List<LocalImportedFile> imported = new(paths.Count);
        remaining = maxTotalBytes;
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string scopedKey = "folder-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"praxis-folder-v1\n{batchKey}\n{path}")));
            CapturedOriginal accepted = await ImportFileAsync(path, Math.Min(maxFileBytes, remaining), scopedKey, cancellationToken);
            remaining -= accepted.Length;
            imported.Add(new LocalImportedFile(path, accepted));
        }

        return imported;
    }

    internal string ValidateSourcePath(string sourcePath, bool expectDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!Path.IsPathFullyQualified(sourcePath))
        {
            throw new ArgumentException("A selected source path must be absolute.", nameof(sourcePath));
        }

        string full = Path.GetFullPath(sourcePath);
        string dataRoot = CatalogPaths.ResolveExisting(catalog.DataDirectory);
        if (CatalogPaths.Contains(dataRoot, CatalogPaths.ResolveExisting(full)))
        {
            throw new InvalidOperationException("The data store cannot be imported as a source.");
        }

        FileAttributes attributes = File.GetAttributes(full);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("A selected source path cannot be a symbolic link.");
        }

        bool isDirectory = (attributes & FileAttributes.Directory) != 0;
        return isDirectory != expectDirectory
            ? throw new InvalidDataException("The selected source has an unexpected file type.")
            : full;
    }

    private List<string> DiscoverFiles(string directory, int maxFiles, int maxDepth, CancellationToken cancellationToken)
    {
        List<string> result = new();
        Stack<(string Directory, int Depth)> stack = new();
        stack.Push((directory, 0));
        int remainingEntries = checked(maxFiles * 4);
        while (stack.TryPop(out (string Directory, int Depth) pending))
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<string> entries = new();
            foreach (string entry in Directory.EnumerateFileSystemEntries(pending.Directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (--remainingEntries < 0)
                {
                    throw new InvalidDataException("The folder contains too many entries.");
                }

                entries.Add(entry);
            }

            entries.Sort(StringComparer.Ordinal);
            for (int index = entries.Count - 1; index >= 0; index--)
            {
                string path = entries[index];
                FileAttributes attributes = File.GetAttributes(path);
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

                    stack.Push((ValidateSourcePath(path, expectDirectory: true), pending.Depth + 1));
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
