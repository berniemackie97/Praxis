namespace Praxis.Storage;

internal static class CatalogPaths
{
    private const int MaximumLinkDepth = 40;

    internal static bool Contains(string directory, string path)
    {
        var relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal static string ResolveExisting(string path) => ResolveExisting(Path.GetFullPath(path), 0);

    private static string ResolveExisting(string path, int linkDepth)
    {
        if (linkDepth > MaximumLinkDepth)
        {
            throw new InvalidDataException("The path contains too many symbolic links.");
        }

        var root = Path.GetPathRoot(path)!;
        var current = root;
        foreach (var component in path[root.Length..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = Path.Combine(current, component);
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                current = entry;
                continue;
            }

            FileSystemInfo link = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(entry)
                : new FileInfo(entry);
            var target = link.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? throw new InvalidDataException("The path contains a broken symbolic link.");
            current = ResolveExisting(target, linkDepth + 1);
        }

        return current;
    }
}
