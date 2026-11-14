namespace Praxis.Application.Activity;

/// <summary>One finished model call with its outcome, the file it was about, and how long it took when known.</summary>
public sealed record RecentModelCall(
    DateTimeOffset StartedUtc, string Tier, string ProviderId, string? Model, string Purpose, string Outcome, string? FileName,
    int? DurationMs, int InputTokens, int OutputTokens);
