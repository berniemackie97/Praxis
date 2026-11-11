namespace Praxis.Architecture.Tests;

/// <summary>The repository's production C# sources, located from the test output directory.</summary>
internal static class SourceTree
{
    internal static readonly string Root = FindRoot();

    internal static string Src => Path.Combine(Root, "src");

    /// <summary>Production source files with their path relative to <c>src</c>, using forward slashes.</summary>
    internal static IEnumerable<(string Path, string Relative)> ProductionFiles() =>
        Directory.EnumerateFiles(Src, "*.cs", SearchOption.AllDirectories)
            .Select(path => (path, Relative: Path.GetRelativePath(Src, path).Replace('\\', '/')))
            .Where(file => !file.Relative.Split('/').Any(segment => segment is "bin" or "obj"));

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Praxis.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The Praxis repository root was not found above the test output.");
    }
}
