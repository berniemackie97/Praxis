namespace Praxis.Application.Desktop.Contracts;

/// <summary>A connected provider's roles, today's calls against its daily limit, recent failures and its latest call.</summary>
public sealed record ProviderActivity(
    string ProviderId, bool Enabled, int DailyLimit, int CallsToday, int FailuresLastHour, DateTimeOffset? LastCallUtc, string? LastOutcome,
    IReadOnlyList<string> Roles);
