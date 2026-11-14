namespace Praxis.Application.Activity;

/// <summary>How much one provider was asked today (UTC), how many calls failed in the last hour, and its latest call.</summary>
public sealed record ProviderUsage(string ProviderId, int CallsToday, int FailuresLastHour, DateTimeOffset? LastCallUtc, string? LastOutcome);
