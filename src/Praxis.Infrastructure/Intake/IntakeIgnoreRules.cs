using System.Text.RegularExpressions;

namespace Praxis.Infrastructure.Intake;

/// <summary>
/// What watched-folder capture never takes: dependency and build output (which would bury real
/// sources and exceed scan limits), version-control internals, tool caches, and secrets files.
/// </summary>
internal static partial class IntakeIgnoreRules
{
    /// <summary>The file every Python virtual environment carries at its root, whatever the folder is called.</summary>
    internal const string VirtualEnvironmentMarker = "pyvenv.cfg";

    // Unambiguous tool folders, ignored anywhere.
    private static readonly HashSet<string> ToolDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", "node_modules", "bower_components", ".build", "DerivedData", "__pycache__", ".venv", ".tox", ".mypy_cache",
        ".pytest_cache", ".ruff_cache", "Pods", "Carthage", ".gradle", ".idea", ".vs", ".vscode-test", ".next", ".nuxt", ".svelte-kit",
        ".angular", ".turbo", ".parcel-cache", ".terraform", ".swiftpm", ".dart_tool", "elm-stuff", ".stack-work", ".expo", ".serverless",
        ".playwright-cli", ".playwright-mcp", ".ipynb_checkpoints", ".obsidian"
    };

    // Generic names that are build or dependency output only inside a code project; elsewhere a
    // folder called "Build" or "out" may be the owner's own notes.
    private static readonly HashSet<string> ProjectOutputDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "build", "dist", "out", "target", "venv", "vendor", "coverage", "_build", "deps", ".cache"
    };

    private static readonly HashSet<string> SecretExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pem", ".key", ".p12", ".pfx", ".keystore", ".jks", ".kdbx", ".ppk", ".mobileprovision"
    };

    private static readonly HashSet<string> SecretNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", ".netrc", ".npmrc", ".pypirc", "credentials", ".git-credentials"
    };

    /// <summary>True when the directory at <paramref name="path"/> is dependency, build or tool output.</summary>
    internal static bool IsIgnoredDirectory(string path, bool insideProject)
    {
        string name = Path.GetFileName(path);
        return ToolDirectories.Contains(name) || (insideProject && ProjectOutputDirectories.Contains(name)) || IsVirtualEnvironment(path);
    }

    /// <summary>
    /// True for a Python virtual environment. Its name is arbitrary (<c>venv</c>, <c>env</c>, <c>.tooling/venv</c>)
    /// and it is often created outside any recognizable project, so the marker file decides, not the name.
    /// </summary>
    internal static bool IsVirtualEnvironment(string directory) => File.Exists(Path.Combine(directory, VirtualEnvironmentMarker));

    /// <summary>True for files that hold credentials and must never become sources.</summary>
    internal static bool IsSecretFile(string name) =>
        SecretNames.Contains(name) || SecretExtensions.Contains(Path.GetExtension(name))
        || AccountRecoveryCodes().IsMatch(Path.GetFileNameWithoutExtension(name))
        || name.Equals(".env", StringComparison.OrdinalIgnoreCase)
        || (name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".example", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith(".sample", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".template", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when a path under <paramref name="root"/> lies in a tool folder (such as <c>.git</c> or
    /// <c>node_modules</c>), inside a Python virtual environment, or is a secrets file: such sources
    /// were captured by older versions and are retired.
    /// </summary>
    internal static bool IsAlwaysExcluded(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return false;
        }

        string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments[..^1].Any(ToolDirectories.Contains) || IsSecretFile(segments[^1]))
        {
            return true;
        }

        string directory = root;
        foreach (string segment in segments[..^1])
        {
            directory = Path.Combine(directory, segment);
            if (IsVirtualEnvironment(directory))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether any applicable <c>.gitignore</c> (outermost first) excludes the path.</summary>
    internal static bool IsGitIgnored(IReadOnlyList<GitIgnoreRules> rules, string path, bool isDirectory)
    {
        bool ignored = false;
        foreach (GitIgnoreRules rule in rules)
        {
            string relative = Path.GetRelativePath(rule.BaseDirectory, path).Replace('\\', '/');
            if (rule.Decide(relative, isDirectory) is { } decision)
            {
                ignored = decision;
            }
        }

        return ignored;
    }

    // A file named for two-factor backup or recovery codes ("stripe_backup_code.txt", "github-recovery-codes-2.txt");
    // the name must end with the codes so notes such as "recovery_code_review.md" stay ordinary sources.
    [GeneratedRegex(@"(?:^|[^a-z])(?:backup|recovery)[-_ .]?codes?(?:[-_ .]?\d+)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AccountRecoveryCodes();
}
