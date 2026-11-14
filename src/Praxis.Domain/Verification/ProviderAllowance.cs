namespace Praxis.Domain.Verification;

/// <summary>An enabled provider with the number of requests it has started since the start of the UTC day.</summary>
public sealed record ProviderAllowance(VerificationProvider Provider, int StartedToday)
{
    public bool HasAllowance => Provider.WithinLimit(StartedToday);
}
