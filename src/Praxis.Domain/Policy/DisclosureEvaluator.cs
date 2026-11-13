namespace Praxis.Domain.Policy;

/// <summary>
/// The single decision function for remote disclosure. Precedence: local providers always;
/// ADR 0003 local-only security spaces never; explicit active denials; explicit active grants;
/// then the mode. Pure and deterministic, so every path is unit-testable.
/// </summary>
public static class DisclosureEvaluator
{
    public static DisclosureDecision Decide(
        DisclosurePolicy policy, string category, string providerId, bool providerIsLocal, string purpose,
        bool sourceIsLocalOnly, DateTimeOffset now)
    {
        if (providerIsLocal)
        {
            return new DisclosureDecision(true, false, DisclosureDecision.LocalProvider, null);
        }

        if (sourceIsLocalOnly)
        {
            return new DisclosureDecision(false, false, DisclosureDecision.LocalOnlySpace, null);
        }

        DisclosureRule[] matching = policy.Rules
            .Where(rule => rule.Category == category && rule.ProviderId == providerId && rule.Purpose == purpose && rule.IsActive(now))
            .ToArray();
        if (matching.FirstOrDefault(rule => !rule.Allow) is { } denial)
        {
            return new DisclosureDecision(false, false, DisclosureDecision.DeniedByRule, denial.RuleId);
        }

        if (policy.Mode == DisclosureModes.LocalOnly)
        {
            return new DisclosureDecision(false, false, DisclosureDecision.ModeLocalOnly, null);
        }

        if (matching.FirstOrDefault(rule => rule.Allow) is { } grant)
        {
            return new DisclosureDecision(true, false, DisclosureDecision.GrantedByRule, grant.RuleId);
        }

        return policy.Mode switch
        {
            DisclosureModes.AllowAll => new DisclosureDecision(true, false, DisclosureDecision.ModeAllowAll, null),
            DisclosureModes.AskEachTime => new DisclosureDecision(false, true, DisclosureDecision.ModeAskEachTime, null),
            _ => new DisclosureDecision(false, true, DisclosureDecision.NoGrant, null)
        };
    }
}
