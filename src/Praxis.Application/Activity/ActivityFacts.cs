namespace Praxis.Application.Activity;

/// <summary>What the catalog records about work in progress: running jobs, stage counts, model calls and intake outcomes.</summary>
public sealed record ActivityFacts(
    IReadOnlyList<RunningJob> Running, IReadOnlyList<StageCount> Stages, IReadOnlyList<RecentModelCall> RecentCalls,
    IReadOnlyList<ProviderUsage> Providers, IReadOnlyList<IntakeOutcomeCount> Outcomes);
