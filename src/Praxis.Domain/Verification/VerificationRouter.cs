namespace Praxis.Domain.Verification;

/// <summary>Chooses the provider that receives an approved verification request.</summary>
public static class VerificationRouter
{
    /// <summary>
    /// The requested provider, or otherwise the cheapest enabled provider (by cost weight, then
    /// identifier) that still has daily allowance. Returns null when none qualifies.
    /// </summary>
    public static VerificationProvider? Route(IEnumerable<ProviderAllowance> enabled, string? requestedProviderId)
    {
        ArgumentNullException.ThrowIfNull(enabled);
        return enabled
            .Where(candidate => requestedProviderId is null || candidate.Provider.ProviderId == requestedProviderId)
            .OrderBy(candidate => candidate.Provider.CostWeight)
            .ThenBy(candidate => candidate.Provider.ProviderId, StringComparer.Ordinal)
            .FirstOrDefault(candidate => candidate.HasAllowance)?.Provider;
    }
}
