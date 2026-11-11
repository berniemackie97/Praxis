namespace Praxis.Domain.Common;

/// <summary>Lexical containment of absolute, already canonical paths.</summary>
public static class PathScope
{
    /// <summary>True when <paramref name="path"/> is <paramref name="directory"/> or lies beneath it.</summary>
    public static bool Contains(string directory, string path)
    {
        string relative = Path.GetRelativePath(directory, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
               && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }
}
