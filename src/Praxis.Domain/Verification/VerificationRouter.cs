namespace Praxis.Domain.Verification;

/// <summary>Choose the specifically approved verifier; never silently escalate to external inference.</summary>
public static class VerificationRouter
{
    public static VerificationProvider? Route(IEnumerable<ProviderAllowance> enabled, string? requestedProviderId)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        return enabled
            .Where(candidate => requestedProviderId is null
                ? candidate.Provider.IsLocal
                : candidate.Provider.ProviderId == requestedProviderId)
            .Where(candidate => candidate.Provider.Enabled && candidate.HasAllowance)
            .OrderBy(candidate => candidate.Provider.CostWeight)
            .ThenBy(candidate => candidate.Provider.ProviderId, StringComparer.Ordinal)
            .FirstOrDefault()?.Provider;
    }
}
