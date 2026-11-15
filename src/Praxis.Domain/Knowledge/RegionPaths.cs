using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Praxis.Domain.Knowledge;

/// <summary>
/// Derives knowledge regions from the owner's folder structure, the strongest grouping evidence
/// Praxis has before relations are reviewed: a repository or folder the owner made is a region,
/// its subfolders are child regions. Generic code containers such as <c>src</c> are looked through
/// so a solution's projects become the children. Keys are root-relative, so identities stay stable
/// across rebuilds and machines.
/// </summary>
public static partial class RegionPaths
{
    private static readonly HashSet<string> Containers = new(StringComparer.OrdinalIgnoreCase)
    {
        "src", "source", "sources", "lib", "libs", "app", "apps", "packages", "projects", "modules", "services"
    };

    /// <summary>The placement of <paramref name="locator"/> under the deepest root containing it, or null.</summary>
    public static RegionPlacement? Place(string locator, IReadOnlyList<string> roots)
    {
        string? root = null;
        foreach (string candidate in roots)
        {
            if (locator.Length > candidate.Length + 1 && locator.StartsWith(candidate, StringComparison.Ordinal)
                && locator[candidate.Length] == '/' && (root is null || candidate.Length > root.Length))
            {
                root = candidate;
            }
        }

        if (root is null)
        {
            return null;
        }

        string rootName = root[(root.LastIndexOf('/') + 1)..];
        string[] segments = locator[(root.Length + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length <= 1)
        {
            return new RegionPlacement(rootName + "/", "Loose files in " + Label(rootName), true, null, null);
        }

        string topKey = rootName + "/" + segments[0];
        if (segments.Length == 2)
        {
            return new RegionPlacement(topKey, Label(segments[0]), false, null, null);
        }

        // Dot-folders (.github, .vscode, .claude) hold configuration, not a subject: their files
        // belong to the top region without forming a child region of their own.
        string child = segments[1];
        if (child.StartsWith('.'))
        {
            return new RegionPlacement(topKey, Label(segments[0]), false, null, null);
        }

        return Containers.Contains(child) && segments.Length >= 4
            ? new RegionPlacement(topKey, Label(segments[0]), false, topKey + "/" + child + "/" + segments[2], Label(segments[2]))
            : new RegionPlacement(topKey, Label(segments[0]), false, topKey + "/" + child, Label(child));
    }

    /// <summary>The absolute folder of a non-loose top region, for matching repository maps.</summary>
    public static string? TopFolder(string locator, IReadOnlyList<string> roots, RegionPlacement placement)
    {
        if (placement.Loose)
        {
            return null;
        }

        string? root = roots.Where(candidate => locator.StartsWith(candidate + "/", StringComparison.Ordinal))
            .OrderByDescending(candidate => candidate.Length).FirstOrDefault();
        return root is null ? null : root + "/" + locator[(root.Length + 1)..].Split('/')[0];
    }

    /// <summary>A stable opaque identity for a region key.</summary>
    public static string Id(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];

    /// <summary>
    /// A readable folder name: an ordering prefix such as <c>01_</c> is dropped and underscores become
    /// spaces, so <c>01_Formation_and_Governance</c> reads "Formation and Governance".
    /// </summary>
    public static string Label(string folder)
    {
        string trimmed = OrderingPrefix().Replace(folder, "");
        string label = (trimmed.Length > 0 ? trimmed : folder).Replace('_', ' ').Trim();
        return label.Length > 0 ? label : folder;
    }

    [GeneratedRegex(@"^\d{1,3}[_\- ]+(?=\D)")]
    private static partial Regex OrderingPrefix();
}
