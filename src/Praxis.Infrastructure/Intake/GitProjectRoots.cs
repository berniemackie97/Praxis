using Praxis.Application.Projects;
using Praxis.Domain.Common;

namespace Praxis.Infrastructure.Intake;

/// <summary>Finds Git markers above captured sources, without reading Git metadata or traversing the repo.</summary>
public static class GitProjectRoots
{
    public static IReadOnlyCollection<string> Find(IReadOnlyList<ProjectSource> sources, IReadOnlyList<string> approvedRoots)
    {
        HashSet<string> seen = new(StringComparer.Ordinal);
        HashSet<string> repositories = new(StringComparer.Ordinal);
        foreach (ProjectSource source in sources)
        {
            string? root = approvedRoots.FirstOrDefault(candidate => PathScope.Contains(candidate, source.Locator));
            if (root is null)
            {
                continue;
            }

            for (string? folder = Path.GetDirectoryName(source.Locator); folder is not null && PathScope.Contains(root, folder);
                 folder = Path.GetDirectoryName(folder))
            {
                if (!seen.Add(folder))
                {
                    break;
                }

                if (HasMarker(folder, root))
                {
                    repositories.Add(folder);
                }
            }
        }

        return repositories;
    }

    private static bool HasMarker(string folder, string root)
    {
        try
        {
            // Do not probe past a link substituted into a captured path.
            for (string? current = folder; current is not null; current = Path.GetDirectoryName(current))
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    return false;
                }

                if (current == root)
                {
                    return !File.GetAttributes(Path.Combine(folder, ".git")).HasFlag(FileAttributes.ReparsePoint);
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }
}
