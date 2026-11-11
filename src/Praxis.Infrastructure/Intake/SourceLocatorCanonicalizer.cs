using Praxis.Infrastructure.FileSystem;
using Praxis.Infrastructure.Persistence;

namespace Praxis.Infrastructure.Intake;

/// <summary>
/// Brings stored file locators to their canonical spelling. A locator spelled through a linked
/// ancestor is relocated (identity and history kept, move recorded); when the canonical
/// spelling already belongs to another source, the alias is folded into it. Paths that cannot
/// be inspected right now (for example on an unavailable drive) are left for a later run.
/// </summary>
internal sealed class SourceLocatorCanonicalizer(SqliteCatalog catalog, Action<string> report)
{
    internal (int Relocated, int Folded) Run()
    {
        int relocated = 0;
        int folded = 0;
        foreach ((Guid sourceId, string locator, string digest) in catalog.Sources.ActiveFileLocators())
        {
            string canonical;
            try
            {
                canonical = FileSystemPaths.Canonicalize(locator);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                continue;
            }

            if (canonical == locator)
            {
                continue;
            }

            if (catalog.Sources.RelocateObservedFile(locator, canonical, digest))
            {
                relocated++;
            }
            else if (catalog.Sources.FoldAlias(sourceId, canonical))
            {
                folded++;
            }
        }

        if (relocated + folded != 0)
        {
            report($"Normalized source paths: {relocated} relocated, {folded} duplicate spelling(s) folded into their canonical source.");
        }

        return (relocated, folded);
    }
}
