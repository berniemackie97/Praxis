namespace Praxis.Application.Projects;

/// <summary>Files and folders whose presence makes a folder the root of a code project.</summary>
public static class ProjectMarkers
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "package.json", "Cargo.toml", "go.mod", "pyproject.toml", "setup.py", "Package.swift", "pom.xml", "build.gradle",
        "build.gradle.kts", "settings.gradle", "Gemfile", "composer.json", "mix.exs", "CMakeLists.txt", "deno.json", "pubspec.yaml"
    };

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".sln", ".slnx", ".csproj", ".fsproj", ".xcodeproj", ".xcworkspace" };

    /// <summary>True when an entry with this name marks its folder as a project root.</summary>
    public static bool IsMarker(string name) => Names.Contains(name) || Extensions.Contains(Path.GetExtension(name));
}
