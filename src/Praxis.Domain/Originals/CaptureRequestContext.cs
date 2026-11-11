
using Praxis.Domain.Common;

namespace Praxis.Domain.Originals;

/// <summary>Trusted local entry point and checked file scope, not an authentication credential.</summary>
public sealed record CaptureRequestContext(string Route, string AuthorityBasis, string? Scope)
{
    public static readonly CaptureRequestContext Unattributed =
        new("direct_local", "unattributed_local_call", null);

    public void Validate(string locator)
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
            : PathScope.Contains(Scope, locator) &&
              !string.Equals(Scope, locator, StringComparison.Ordinal);
        if (!allowed)
        {
            throw new InvalidOperationException("The source is outside the declared capture scope.");
        }
    }
}
