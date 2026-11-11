namespace Praxis.Infrastructure.Persistence;

/// <summary>Layout and access rules for the private catalog data directory.</summary>
internal static class CatalogDirectory
{
    internal const string DatabaseFile = "catalog.sqlite";
    internal const string LockFile = "catalog.lock";

    private static readonly HashSet<string> AllowedEntries = new(StringComparer.Ordinal)
    {
        DatabaseFile, DatabaseFile + "-wal", DatabaseFile + "-shm", LockFile,
        "objects", "staging", "derived"
    };

    internal static void Validate(string root)
    {
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("The catalog directory cannot be a symbolic link.");
        }

        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode publicAccess = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            if ((File.GetUnixFileMode(root) & publicAccess) != 0)
            {
                throw new UnauthorizedAccessException("The catalog directory must be private to its owner.");
            }
        }

        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            string name = Path.GetFileName(entry);
            if (!AllowedEntries.Contains(name) || (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("The catalog directory contains an unexpected or linked entry.");
            }

            if (name is "objects" or "staging" or "derived")
            {
                if (!Directory.Exists(entry))
                {
                    throw new InvalidDataException("A reserved catalog directory name is occupied by a file.");
                }
            }
            else if (!File.Exists(entry))
            {
                throw new InvalidDataException("A reserved catalog file name is occupied by a directory.");
            }
        }
    }
}
