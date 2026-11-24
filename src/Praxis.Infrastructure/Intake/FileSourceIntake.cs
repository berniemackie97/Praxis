using System.Security.Cryptography;
using System.Text;
using Praxis.Application.Originals;
using Praxis.Application.Projects;
using Praxis.Domain.Common;
using Praxis.Domain.Intake;
using Praxis.Domain.Originals;
using Praxis.Infrastructure.FileSystem;

namespace Praxis.Infrastructure.Intake;

public sealed class FileSourceIntake(IOriginalStore originals, string catalogDirectory)
{
    public IReadOnlyList<ObservedFile> InspectFolder(string selectedDirectory, int maxFiles, int maxDepth, long maxTotalBytes, CancellationToken cancellationToken = default) =>
        InspectFolderCore(selectedDirectory, maxFiles, maxDepth, maxTotalBytes, false, out _, cancellationToken);

    internal (IReadOnlyList<ObservedFile> Files, int SkippedLinks, int TotalFiles, string? NextCursor) InspectWatchedFolder(
        string directory, int maxFiles, int maxDepth, long maxTotalBytes, CancellationToken cancellationToken,
        string? after = null, Action<string>? onObserved = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxFiles, 10_000);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDepth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxDepth, 16);
        ArgumentOutOfRangeException.ThrowIfNegative(maxTotalBytes);
        string root = ValidateSourcePath(directory, expectDirectory: true);
        PriorityQueue<ObservedFile, string> page = new(Comparer<string>.Create((left, right) => StringComparer.Ordinal.Compare(right, left)));
        TraversalState state = new();
        int total = 0, eligible = 0;
        foreach (string path in WalkFiles(root, 0, false, [], maxDepth, true, state, cancellationToken))
        {
            onObserved?.Invoke(path);
            total = checked(total + 1);
            if (after is not null && StringComparer.Ordinal.Compare(path, after) <= 0)
            {
                continue;
            }

            eligible = checked(eligible + 1);
            if (page.Count == maxFiles && StringComparer.Ordinal.Compare(path, page.Peek().SourcePath) >= 0)
            {
                continue;
            }

            FileInfo info = new(path);
            ObservedFile file = new(path, info.Length, info.LastWriteTimeUtc);
            page.Enqueue(file, path);
            if (page.Count > maxFiles)
            {
                page.Dequeue();
            }
        }

        ObservedFile[] files = page.UnorderedItems.Select(entry => entry.Element).OrderBy(file => file.SourcePath, StringComparer.Ordinal).ToArray();
        // The byte budget bounds one page. A large Inbox continues on the following scan.
        int take = 0;
        long remaining = maxTotalBytes;
        while (take < files.Length && files[take].Length <= remaining)
        {
            remaining -= files[take++].Length;
        }
        if (take == 0 && files.Length > 0)
        {
            throw new InvalidDataException("A file exceeds the watched-folder inspection byte budget.");
        }

        ObservedFile[] selected = files[..take];
        return (selected, state.SkippedLinks, total, eligible > take ? selected[^1].SourcePath : null);
    }

    private IReadOnlyList<ObservedFile> InspectFolderCore(string selectedDirectory, int maxFiles, int maxDepth,
        long maxTotalBytes, bool skipLinks, out int skippedLinks, CancellationToken cancellationToken)
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
        List<string> paths = DiscoverFiles(root, maxFiles, maxDepth, skipLinks, out skippedLinks, cancellationToken);
        List<ObservedFile> files = new(paths.Count);
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
            files.Add(new ObservedFile(path, length, info.LastWriteTimeUtc));
        }

        return files;
    }

    public Task<CapturedOriginal> ImportFileAsync(string sourcePath, long maxBytes, string requestKey, CancellationToken cancellationToken = default) =>
        ImportFileCoreAsync(sourcePath, maxBytes, requestKey, CaptureContexts.SelectedFile(sourcePath), cancellationToken);

    internal Task<CapturedOriginal> ImportWatchedFileAsync(string sourcePath, string approvedRoot, long maxBytes, string requestKey, string expectedDigest, long expectedLength, CancellationToken cancellationToken = default) =>
        ImportFileCoreAsync(sourcePath, maxBytes, requestKey, CaptureContexts.WatchedFolder(approvedRoot), cancellationToken, expectedDigest, expectedLength);

    private async Task<CapturedOriginal> ImportFileCoreAsync(string sourcePath, long maxBytes, string requestKey, CaptureRequestContext capture, CancellationToken cancellationToken, string? expectedDigest = null, long? expectedLength = null)
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
        CapturedOriginal accepted = await originals.CaptureFileAsync(path, input, maxBytes, requestKey, capture, cancellationToken, expectedDigest, expectedLength);
        return accepted.Length > maxBytes
            ? throw new InvalidDataException("The committed original exceeds the import byte limit.")
            : accepted;
    }

    public async Task<IReadOnlyList<ImportedFile>> ImportFolderAsync(string selectedDirectory, string batchKey, int maxFiles, int maxDepth, long maxFileBytes, long maxTotalBytes, CancellationToken cancellationToken = default)
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
        List<string> paths = DiscoverFiles(root, maxFiles, maxDepth, false, out _, cancellationToken);
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

        List<ImportedFile> imported = new(paths.Count);
        CaptureRequestContext capture = CaptureContexts.SelectedFolder(root);
        remaining = maxTotalBytes;
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string scopedKey = "folder-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"praxis-folder-v1\n{batchKey}\n{path}")));
            CapturedOriginal accepted = await ImportFileCoreAsync(path, Math.Min(maxFileBytes, remaining), scopedKey, capture, cancellationToken);
            remaining -= accepted.Length;
            imported.Add(new ImportedFile(path, accepted));
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
        string dataRoot = FileSystemPaths.ResolveExisting(catalogDirectory);
        if (PathScope.Contains(dataRoot, FileSystemPaths.ResolveExisting(full)))
        {
            throw new InvalidOperationException("The data store cannot be imported as a source.");
        }

        FileAttributes attributes = File.GetAttributes(full);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("A selected source path cannot be a symbolic link.");
        }

        bool isDirectory = (attributes & FileAttributes.Directory) != 0;
        // The leaf is not a link, so canonicalizing only normalizes linked ancestors.
        return isDirectory != expectDirectory
            ? throw new InvalidDataException("The selected source has an unexpected file type.")
            : FileSystemPaths.Canonicalize(full);
    }

    private List<string> DiscoverFiles(string directory, int maxFiles, int maxDepth,
        bool skipLinks, out int skippedLinks, CancellationToken cancellationToken)
    {
        TraversalState state = new() { RemainingEntries = checked(maxFiles * 4) };
        List<string> result = new();
        foreach (string path in WalkFiles(directory, 0, false, [], maxDepth, skipLinks, state, cancellationToken))
        {
            if (result.Count >= maxFiles)
            {
                throw new InvalidDataException("The folder contains too many files.");
            }
            result.Add(path);
        }
        skippedLinks = state.SkippedLinks;
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private sealed class TraversalState
    {
        public int? RemainingEntries { get; set; }
        public int SkippedLinks { get; set; }
    }

    private IEnumerable<string> WalkFiles(string directory, int depth, bool insideProject, IReadOnlyList<GitIgnoreRules> inheritedRules,
        int maxDepth, bool skipLinks, TraversalState state, CancellationToken cancellationToken)
    {
        // Enumerate lazily so a large directory cannot allocate an unbounded sorted entry list.
        if (!insideProject)
        {
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (ProjectMarkers.IsMarker(Path.GetFileName(entry)))
                {
                    insideProject = true;
                    break;
                }
            }
        }
        IReadOnlyList<GitIgnoreRules> rules = GitIgnoreRules.Load(directory) is { } local ? [.. inheritedRules, local] : inheritedRules;
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.RemainingEntries is { } remaining)
            {
                if (remaining == 0)
                {
                    throw new InvalidDataException("The folder contains too many entries.");
                }
                state.RemainingEntries = remaining - 1;
            }
            string name = Path.GetFileName(path);
            FileAttributes attributes = File.GetAttributes(path);
            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            // Dependency and build output, version-control internals and secrets are never
            // sources; skipping them before any other check keeps their links and size out of scope.
            if ((isDirectory ? IntakeIgnoreRules.IsIgnoredDirectory(path, insideProject) : IntakeIgnoreRules.IsSecretFile(name))
                || IntakeIgnoreRules.IsGitIgnored(rules, path, isDirectory))
            {
                continue;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (skipLinks)
                {
                    state.SkippedLinks++;
                    continue;
                }

                throw new InvalidDataException("Folder imports cannot follow symbolic links.");
            }

            if (isDirectory)
            {
                if (depth == maxDepth)
                {
                    throw new InvalidDataException("The folder nesting depth exceeds the limit.");
                }

                foreach (string child in WalkFiles(ValidateSourcePath(path, expectDirectory: true), depth + 1, insideProject, rules, maxDepth, skipLinks, state, cancellationToken))
                {
                    yield return child;
                }
                continue;
            }

            // The containing directory is already canonical and outside the catalog, and
            // the leaf attributes above reject links. Capture validates it again before opening.
            yield return path;
        }
    }
}
