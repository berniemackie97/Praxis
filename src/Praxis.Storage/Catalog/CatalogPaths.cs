namespace Praxis.Storage.Catalog;

internal static class CatalogPaths
{
    private const int MaximumLinkDepth = 40;

    internal static bool Contains(string directory, string path)
    {
        string relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal static string ResolveExisting(string path) => ResolveExisting(Path.GetFullPath(path), 0);

    /// <summary>
    /// The canonical spelling of a path: symbolic links in its existing ancestors are resolved
    /// (on macOS <c>/tmp</c> becomes <c>/private/tmp</c>) and any not-yet-existing remainder is
    /// appended unchanged. Two spellings of the same location yield the same result, so source
    /// identities do not depend on how a caller wrote the path.
    /// </summary>
    internal static string Canonicalize(string path)
    {
        string full = Path.GetFullPath(path);
        string existing = full;
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            string? parent = Path.GetDirectoryName(existing);
            if (parent is null)
            {
                return full;
            }

            existing = parent;
        }

        string remainder = Path.GetRelativePath(existing, full);
        string resolved = ResolveExisting(existing);
        return remainder == "." ? resolved : Path.Combine(resolved, remainder);
    }

    private static string ResolveExisting(string path, int linkDepth)
    {
        if (linkDepth > MaximumLinkDepth)
        {
            throw new InvalidDataException("The path contains too many symbolic links.");
        }

        string root = Path.GetPathRoot(path)!;
        string current = root;
        foreach (string component in path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            string entry = Path.Combine(current, component);
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                current = entry;
                continue;
            }

            FileSystemInfo link = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(entry) : new FileInfo(entry);
            string target = link.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? throw new InvalidDataException("The path contains a broken symbolic link.");
            current = ResolveExisting(target, linkDepth + 1);
        }

        return current;
    }
}
