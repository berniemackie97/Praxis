using System.Security.Cryptography;
using System.Text;

namespace Praxis.Application.Projects;

/// <summary>
/// Finds code projects among captured sources: a folder holding a project marker (a solution,
/// package manifest, build file…) is a root; roots nested inside another root belong to it, so a
/// monorepo is one project. Works from source paths alone, in code.
/// </summary>
public static class ProjectDetector
{
    public const int MaximumMembers = 5_000;

    public static IReadOnlyList<ProjectCandidate> Detect(IReadOnlyList<ProjectSource> sources, IReadOnlyCollection<string>? gitRoots = null)
    {
        List<string> roots = sources
            .Where(source => ProjectMarkers.IsMarker(Path.GetFileName(source.Locator)))
            .Select(source => Path.GetDirectoryName(source.Locator)!)
            .Concat(gitRoots ?? [])
            .Distinct(StringComparer.Ordinal)
            .OrderBy(root => root.Length)
            .ToList();
        List<string> outermost = [];
        foreach (string root in roots)
        {
            if (!outermost.Any(parent => IsWithin(root, parent)))
            {
                outermost.Add(root);
            }
        }

        return outermost.Select(root =>
        {
            ProjectSource[] members = sources.Where(source => IsWithin(source.Locator, root)).OrderBy(source => source.Locator, StringComparer.Ordinal)
                .Take(MaximumMembers).ToArray();
            string id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..32];
            return new ProjectCandidate(id, root, Path.GetFileName(root), members);
        }).Where(project => project.Members.Count > 0).ToArray();
    }

    /// <summary>A digest of member paths and contents: it changes exactly when the project's files change.</summary>
    public static string Fingerprint(ProjectCandidate project)
    {
        StringBuilder builder = new();
        foreach (ProjectSource member in project.Members)
        {
            builder.Append(project.Relative(member)).Append('\0').Append(member.Digest).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static bool IsWithin(string path, string root) =>
        path.StartsWith(root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
