using System.Text.Json;
using System.Text.RegularExpressions;

namespace Praxis.Application.Projects;

/// <summary>
/// Dependencies and runnable commands declared by a project's own manifests, parsed in code. These
/// are the only dependencies and commands a project understanding may state.
/// </summary>
public static partial class ProjectManifests
{
    public static bool IsManifest(string fileName) => fileName.ToLowerInvariant() is "package.json" or "cargo.toml" or "go.mod" or "pyproject.toml"
        or "requirements.txt" or "package.swift" or "gemfile" or "composer.json" or "makefile" or "pom.xml" or "build.gradle" or "build.gradle.kts"
        || fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase);

    public static IEnumerable<string> Dependencies(string fileName, string content)
    {
        string name = fileName.ToLowerInvariant();
        IEnumerable<string> found = name switch
        {
            "package.json" or "composer.json" => JsonKeys(content, "dependencies", "devDependencies", "peerDependencies", "require", "require-dev"),
            "cargo.toml" => TomlSection(content, "dependencies").Concat(TomlSection(content, "dev-dependencies")),
            "go.mod" => GoRequire().Matches(content).Select(match => match.Groups[1].Value),
            "pyproject.toml" or "requirements.txt" => PythonRequirement().Matches(content).Select(match => match.Groups[1].Value),
            "package.swift" => SwiftPackage().Matches(content).Select(match => match.Groups[1].Value),
            "gemfile" => Gem().Matches(content).Select(match => match.Groups[1].Value),
            "pom.xml" => MavenArtifact().Matches(content).Select(match => match.Groups[1].Value),
            "build.gradle" or "build.gradle.kts" => Gradle().Matches(content).Select(match => match.Groups[1].Value),
            _ when name.EndsWith("proj", StringComparison.Ordinal) => PackageReference().Matches(content).Select(match => match.Groups[1].Value),
            _ => []
        };
        return found.Where(dependency => dependency.Length is > 0 and <= 120 && dependency != "python");
    }

    /// <summary>Commands a manifest makes runnable, as an owner would type them.</summary>
    public static IEnumerable<string> Commands(string fileName, string content)
    {
        switch (fileName.ToLowerInvariant())
        {
            case "package.json":
                foreach (string script in JsonKeys(content, "scripts"))
                {
                    yield return "npm run " + script;
                    yield return "yarn " + script;
                    yield return "pnpm " + script;
                    if (script is "test" or "start")
                    {
                        yield return "npm " + script;
                    }
                }

                yield return "npm install";
                yield return "npm ci";
                yield return "yarn install";
                yield return "pnpm install";
                break;
            case "cargo.toml":
                foreach (string verb in new[] { "build", "test", "run", "check", "build --release", "run --release" })
                {
                    yield return "cargo " + verb;
                }

                break;
            case "go.mod":
                foreach (string verb in new[] { "build ./...", "test ./...", "run .", "build", "test" })
                {
                    yield return "go " + verb;
                }

                break;
            case "package.swift":
                foreach (string verb in new[] { "build", "test", "run" })
                {
                    yield return "swift " + verb;
                }

                break;
            case "makefile":
                foreach (Match target in MakeTarget().Matches(content))
                {
                    yield return "make " + target.Groups[1].Value;
                }

                yield return "make";
                break;
            case "pyproject.toml" or "requirements.txt":
                yield return "pip install -r requirements.txt";
                yield return "pip install -e .";
                yield return "pytest";
                break;
            case var name when name.EndsWith("proj", StringComparison.Ordinal) || name.EndsWith(".sln", StringComparison.Ordinal) ||
                               name.EndsWith(".slnx", StringComparison.Ordinal):
                foreach (string verb in new[] { "build", "test", "run", "restore", "format" })
                {
                    yield return "dotnet " + verb;
                }

                break;
        }
    }

    private static IEnumerable<string> JsonKeys(string content, params string[] sections)
    {
        List<string> keys = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 16, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            foreach (string section in sections)
            {
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(section, out JsonElement value) &&
                    value.ValueKind == JsonValueKind.Object)
                {
                    keys.AddRange(value.EnumerateObject().Select(property => property.Name));
                }
            }
        }
        catch (JsonException)
        {
            // Not valid JSON: it declares nothing.
        }

        return keys;
    }

    private static IEnumerable<string> TomlSection(string content, string section)
    {
        bool inside = false;
        foreach (string raw in content.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('['))
            {
                inside = line == $"[{section}]";
                continue;
            }

            if (inside && line.Length > 0 && !line.StartsWith('#') && line.Contains('='))
            {
                yield return line[..line.IndexOf('=')].Trim().Trim('"');
            }
        }
    }

    [GeneratedRegex(@"^\s*(?:require\s+)?([\w./-]+\.[\w./-]+)\s+v[\d.]+", RegexOptions.Multiline, 1_000)]
    private static partial Regex GoRequire();

    [GeneratedRegex(@"^\s*""?([A-Za-z][\w.-]*)(?:\[[^\]]*\])?\s*(?:[<>=!~]=?|""|,|$)", RegexOptions.Multiline, 1_000)]
    private static partial Regex PythonRequirement();

    [GeneratedRegex(@"\.package\s*\(\s*(?:name:\s*""[^""]+""\s*,\s*)?url:\s*""[^""]*/([\w.-]+?)(?:\.git)?""", RegexOptions.None, 1_000)]
    private static partial Regex SwiftPackage();

    [GeneratedRegex(@"^\s*gem\s+['""]([\w.-]+)['""]", RegexOptions.Multiline, 1_000)]
    private static partial Regex Gem();

    [GeneratedRegex(@"<artifactId>([\w.-]+)</artifactId>", RegexOptions.None, 1_000)]
    private static partial Regex MavenArtifact();

    [GeneratedRegex(@"(?:implementation|api|testImplementation)\s*\(?\s*['""]([\w.-]+:[\w.-]+)", RegexOptions.None, 1_000)]
    private static partial Regex Gradle();

    [GeneratedRegex(@"<PackageReference\s+Include=""([^""]+)""", RegexOptions.None, 1_000)]
    private static partial Regex PackageReference();

    [GeneratedRegex(@"^([A-Za-z][\w-]*)\s*:(?!=)", RegexOptions.Multiline, 1_000)]
    private static partial Regex MakeTarget();
}
