namespace Praxis.Domain.Models;

/// <summary>The provider (a configured verifier profile) that fills one tier.</summary>
public sealed record TierAssignment(string Tier, string ProviderId);
