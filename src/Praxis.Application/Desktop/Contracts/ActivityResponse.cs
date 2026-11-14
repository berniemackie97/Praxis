using Praxis.Application.Activity;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>What Praxis is doing now: model calls in flight, running jobs, stage counts, provider usage, recent calls and what needs attention.</summary>
public sealed record ActivityResponse(
    IReadOnlyList<ModelCallInProgress> Calling, IReadOnlyList<RunningJob> Running, IReadOnlyList<StageCount> Stages,
    IReadOnlyList<ProviderActivity> Providers, IReadOnlyList<RecentModelCall> RecentCalls, IReadOnlyList<IntakeOutcomeCount> Outcomes,
    IReadOnlyList<ActivityNotice> Notices, double? LocalSecondsPerThousandCharacters);
