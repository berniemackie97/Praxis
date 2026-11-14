namespace Praxis.Domain.Models;

/// <summary>
/// The provider that fills an intake tier, with an optional model and reasoning effort that
/// override the provider's own defaults for that tier only (so one plan can serve two tiers).
/// </summary>
public sealed record TierAssignment(string Tier, string ProviderId, string? Model = null, string? Effort = null);
