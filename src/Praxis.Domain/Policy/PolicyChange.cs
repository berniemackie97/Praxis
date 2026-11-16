namespace Praxis.Domain.Policy;

/// <summary>
/// One requested change to the policy. Exactly one field group is set.
/// </summary>
public sealed record PolicyChange(
    string? Mode = null, DisclosureRule? AddRule = null, Guid? RemoveRuleId = null,
    string? FallbackCategory = null, IReadOnlyList<string>? FallbackChain = null, string? ToolPermission = null)
{
    public void Validate()
    {
        int set = (Mode is null ? 0 : 1) + (AddRule is null ? 0 : 1) + (RemoveRuleId is null ? 0 : 1) +
                  (FallbackCategory is null ? 0 : 1) + (ToolPermission is null ? 0 : 1);
        if (set != 1 || (Mode is not null && !DisclosureModes.All.Contains(Mode, StringComparer.Ordinal)) ||
            (ToolPermission is not null && !ToolPermissions.All.Contains(ToolPermission, StringComparer.Ordinal)) ||
            (FallbackCategory is not null && (!DataCategories.IsValid(FallbackCategory) || FallbackChain is null || !FallbackSteps.IsValidChain(FallbackChain))))
        {
            throw new ArgumentException("The policy change is invalid.");
        }

        AddRule?.Validate();
    }

    /// <summary>True when applying the change could disclose more or permit more than today.</summary>
    public bool Widens(DisclosurePolicy current)
    {
        Validate();
        if (Mode is not null)
        {
            return DisclosureModes.Permissiveness(Mode) > DisclosureModes.Permissiveness(current.Mode);
        }

        if (ToolPermission is not null)
        {
            return ToolPermissions.Permissiveness(ToolPermission) > ToolPermissions.Permissiveness(current.ToolPermission);
        }

        if (AddRule is not null)
        {
            return AddRule.Allow;
        }

        if (RemoveRuleId is { } id)
        {
            // Removing a denial can widen; removing a grant narrows.
            return current.Rules.FirstOrDefault(rule => rule.RuleId == id) is { Allow: false };
        }

        // Fallbacks only choose among local processing, holding and indexing: none discloses.
        return false;
    }
}
