using System.Text;
using System.Text.RegularExpressions;

namespace Praxis.Infrastructure.Intake;

/// <summary>
/// The patterns of one <c>.gitignore</c> file, applied to paths below the folder that holds it:
/// comments, negation, anchoring, directory-only patterns, <c>*</c>, <c>?</c>, character classes and
/// <c>**</c>. The last matching pattern decides, as in git.
/// </summary>
internal sealed class GitIgnoreRules
{
    internal const int MaximumBytes = 64 * 1024;
    private const int MaximumPatterns = 1_000;
    private readonly List<(Regex Pattern, bool Negated, bool DirectoryOnly)> patterns = [];

    private GitIgnoreRules(string baseDirectory) => BaseDirectory = baseDirectory;

    internal string BaseDirectory { get; }

    /// <summary>Reads the folder's <c>.gitignore</c>, or returns null when there is none or it is unusable.</summary>
    internal static GitIgnoreRules? Load(string directory)
    {
        string path = Path.Combine(directory, ".gitignore");
        try
        {
            FileInfo info = new(path);
            if (!info.Exists || info.Length > MaximumBytes || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return null;
            }

            return Parse(directory, File.ReadAllText(path, Encoding.UTF8));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static GitIgnoreRules Parse(string baseDirectory, string content)
    {
        GitIgnoreRules rules = new(baseDirectory);
        foreach (string raw in content.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#') || rules.patterns.Count == MaximumPatterns)
            {
                continue;
            }

            line = TrimUnescapedTrailingSpaces(line);
            bool negated = line.StartsWith('!');
            if (negated)
            {
                line = line[1..];
            }
            else if (line.StartsWith("\\!", StringComparison.Ordinal) || line.StartsWith("\\#", StringComparison.Ordinal))
            {
                line = line[1..];
            }

            bool directoryOnly = line.EndsWith('/');
            line = line.TrimEnd('/');
            if (line.Length == 0)
            {
                continue;
            }

            // A slash anywhere but the end anchors the pattern to this folder.
            bool anchored = line.Contains('/');
            line = line.TrimStart('/');
            string expression = (anchored ? "^" : "^(?:.*/)?") + Translate(line) + "(?:/.*)?$";
            rules.patterns.Add((new Regex(expression, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking), negated, directoryOnly));
        }

        return rules;
    }

    /// <summary>
    /// True or false when a pattern decides for <paramref name="relativePath"/> (slash-separated,
    /// relative to <see cref="BaseDirectory"/>), or null when none matches.
    /// </summary>
    internal bool? Decide(string relativePath, bool isDirectory)
    {
        bool? decision = null;
        foreach ((Regex pattern, bool negated, bool directoryOnly) in patterns)
        {
            // A directory-only pattern matches the directory itself or anything beneath it.
            if (directoryOnly && !isDirectory && !pattern.IsMatch(Path.GetDirectoryName(relativePath)?.Replace('\\', '/') ?? string.Empty))
            {
                continue;
            }

            if (pattern.IsMatch(relativePath))
            {
                decision = !negated;
            }
        }

        return decision;
    }

    private static string Translate(string glob)
    {
        StringBuilder builder = new();
        for (int index = 0; index < glob.Length; index++)
        {
            char character = glob[index];
            switch (character)
            {
                case '*' when index + 1 < glob.Length && glob[index + 1] == '*':
                    bool leadingSlash = index + 2 < glob.Length && glob[index + 2] == '/';
                    builder.Append(leadingSlash ? "(?:.*/)?" : ".*");
                    index += leadingSlash ? 2 : 1;
                    break;
                case '*':
                    builder.Append("[^/]*");
                    break;
                case '?':
                    builder.Append("[^/]");
                    break;
                case '[':
                    int close = glob.IndexOf(']', index + 1);
                    if (close < 0)
                    {
                        builder.Append("\\[");
                        break;
                    }

                    string set = glob[(index + 1)..close].Replace("\\", "\\\\", StringComparison.Ordinal);
                    builder.Append('[').Append(set.StartsWith('!') ? "^" + set[1..] : set).Append(']');
                    index = close;
                    break;
                case '\\' when index + 1 < glob.Length:
                    builder.Append(Regex.Escape(glob[++index].ToString()));
                    break;
                default:
                    builder.Append(Regex.Escape(character.ToString()));
                    break;
            }
        }

        return builder.ToString();
    }

    private static string TrimUnescapedTrailingSpaces(string line)
    {
        int end = line.Length;
        while (end > 0 && line[end - 1] == ' ' && (end < 2 || line[end - 2] != '\\'))
        {
            end--;
        }

        return line[..end];
    }
}
