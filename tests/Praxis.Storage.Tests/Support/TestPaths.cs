using Praxis.Storage.Catalog;

namespace Praxis.Storage.Tests.Support;

/// <summary>
/// The system temporary folder in canonical form (on macOS <c>/private/var/...</c> rather than
/// the <c>/var/...</c> link), matching how intake records source locators.
/// </summary>
internal static class TestPaths
{
    internal static readonly string Temp = CatalogPaths.Canonicalize(Path.GetTempPath());
}
