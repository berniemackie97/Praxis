using Praxis.Storage.Catalog;

namespace Praxis.Storage.Originals;

/// <summary>Trusted local entry point and checked file scope, not an authentication credential.</summary>
internal sealed record CaptureRequestContext(string Route, string AuthorityBasis, string? Scope)
{
    internal static readonly CaptureRequestContext Unattributed =
        new("direct_local", "unattributed_local_call", null);

    internal static CaptureRequestContext SelectedFile(string path) =>
        new("selected_file", "owner_selected", CatalogPaths.Canonicalize(path));

    internal static CaptureRequestContext SelectedFolder(string root) =>
        new("selected_folder", "owner_selected", CatalogPaths.Canonicalize(root));

    internal static CaptureRequestContext WatchedFolder(string root) =>
        new("watched_folder", "owner_configured", CatalogPaths.Canonicalize(root));

    internal static CaptureRequestContext QuickNote(string root) =>
        new("quick_note", "owner_submitted", CatalogPaths.Canonicalize(root));

    internal void Validate(string locator)
    {
        if (Route == "direct_local")
        {
            if (this != Unattributed)
            {
                throw new InvalidOperationException("Direct captures cannot claim a scoped authority.");
            }

            return;
        }

        string requiredBasis = Route switch
        {
            "selected_file" or "selected_folder" => "owner_selected",
            "watched_folder" => "owner_configured",
            "quick_note" => "owner_submitted",
            _ => throw new ArgumentException("The capture route is invalid.")
        };
        if (AuthorityBasis != requiredBasis || Scope is null || Scope.Length > 4096
            || !Path.IsPathFullyQualified(Scope))
        {
            throw new ArgumentException("The capture scope or authority basis is invalid.");
        }

        bool allowed = Route == "selected_file"
            ? string.Equals(Scope, locator, StringComparison.Ordinal)
            : CatalogPaths.Contains(Scope, locator) &&
              !string.Equals(Scope, locator, StringComparison.Ordinal);
        if (!allowed)
        {
            throw new InvalidOperationException("The source is outside the declared capture scope.");
        }
    }
}
