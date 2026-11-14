using System.Text;

namespace Praxis.Application.Projects;

/// <summary>
/// A bounded picture of a code project built in code: the file tree, languages by size, the
/// dependencies and commands its manifests declare, its README and what each file defines. One
/// strong-tier call reads this instead of thousands of files.
/// </summary>
public sealed record RepositoryMap(
    string Text, IReadOnlySet<string> Paths, IReadOnlySet<string> Dependencies, IReadOnlySet<string> Commands, string Readme,
    IReadOnlyDictionary<string, int> Languages, IReadOnlyList<ProjectSource> Disclosed)
{
    public const int MaximumCharacters = 24_000;
    private const int TreeEntries = 160;
    private const int ReadmeCharacters = 4_000;
    private const int OutlineCharacters = 8_000;

    /// <summary>Builds the map from the members whose text may be shown; <paramref name="text"/> returns a member's read text or null.</summary>
    public static RepositoryMap Build(ProjectCandidate project, IReadOnlyList<ProjectSource> disclosed, Func<ProjectSource, string?> text)
    {
        HashSet<string> paths = new(StringComparer.Ordinal);
        HashSet<string> dependencies = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> commands = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> languages = new(StringComparer.OrdinalIgnoreCase);
        StringBuilder outlines = new();
        string readme = string.Empty;
        foreach (ProjectSource member in disclosed)
        {
            string relative = project.Relative(member);
            paths.Add(relative);
            for (string? folder = Path.GetDirectoryName(relative); !string.IsNullOrEmpty(folder); folder = Path.GetDirectoryName(folder))
            {
                paths.Add(folder.Replace('\\', '/') + "/");
            }
        }

        foreach (ProjectSource member in disclosed)
        {
            string relative = project.Relative(member);
            string name = Path.GetFileName(relative);
            string extension = Path.GetExtension(relative);
            bool manifest = ProjectManifests.IsManifest(name) || name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
                            name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);
            bool isReadme = readme.Length == 0 && name.StartsWith("readme", StringComparison.OrdinalIgnoreCase) && !relative.Contains('/');
            IReadOnlyList<string> symbols = [];
            string? content = null;
            if (manifest || isReadme || Language(extension) is not null)
            {
                content = text(member);
            }

            if (content is null)
            {
                continue;
            }

            if (Language(extension) is { } language)
            {
                languages[language] = languages.GetValueOrDefault(language) + content.Length;
                symbols = CodeOutline.Extract(content, extension);
            }

            if (manifest)
            {
                dependencies.UnionWith(ProjectManifests.Dependencies(name, content));
                commands.UnionWith(ProjectManifests.Commands(name, content));
            }

            if (isReadme)
            {
                readme = content.Length > ReadmeCharacters ? content[..ReadmeCharacters] : content;
            }

            if (symbols.Count > 0 && outlines.Length < OutlineCharacters)
            {
                outlines.Append(relative).Append(": ").Append(string.Join("; ", symbols)).Append('\n');
            }
        }

        StringBuilder map = new();
        map.Append("Project folder: ").Append(project.Name).Append('\n');
        map.Append("Languages (characters): ").Append(string.Join(", ", languages.OrderByDescending(pair => pair.Value).Select(pair => $"{pair.Key} {pair.Value}")))
            .Append("\n\nFiles (").Append(disclosed.Count).Append("):\n");
        foreach (string path in paths.Where(path => !path.EndsWith('/')).Order(StringComparer.Ordinal).Take(TreeEntries))
        {
            map.Append(path).Append('\n');
        }

        if (disclosed.Count > TreeEntries)
        {
            map.Append("… and ").Append(disclosed.Count - TreeEntries).Append(" more\n");
        }

        map.Append("\nDeclared dependencies: ").Append(dependencies.Count == 0 ? "none" : string.Join(", ", dependencies.Order(StringComparer.OrdinalIgnoreCase).Take(200)))
            .Append("\n\nDeclared commands: ").Append(commands.Count == 0 ? "none" : string.Join("; ", commands.Order(StringComparer.OrdinalIgnoreCase).Take(80)))
            .Append("\n\nREADME:\n").Append(readme.Length == 0 ? "(none)" : readme)
            .Append("\n\nWhat files define:\n").Append(outlines);
        string bounded = map.Length > MaximumCharacters ? map.ToString(0, MaximumCharacters) : map.ToString();
        return new RepositoryMap(bounded, paths, dependencies, commands, readme, languages, disclosed);
    }

    private static string? Language(string extension) => extension.ToLowerInvariant() switch
    {
        ".cs" => "C#",
        ".fs" => "F#",
        ".swift" => "Swift",
        ".ts" or ".tsx" => "TypeScript",
        ".js" or ".jsx" or ".mjs" or ".cjs" => "JavaScript",
        ".py" => "Python",
        ".go" => "Go",
        ".rs" => "Rust",
        ".java" => "Java",
        ".kt" or ".kts" => "Kotlin",
        ".rb" => "Ruby",
        ".php" => "PHP",
        ".c" or ".h" => "C",
        ".cpp" or ".hpp" or ".cc" => "C++",
        ".m" or ".mm" => "Objective-C",
        ".scala" => "Scala",
        _ => null
    };
}
