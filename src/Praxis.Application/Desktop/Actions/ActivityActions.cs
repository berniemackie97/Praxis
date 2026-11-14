using Praxis.Application.Activity;
using Praxis.Application.Desktop.Contracts;
using Praxis.Application.Policy;
using Praxis.Application.Verification;
using Praxis.Domain.Models;
using Praxis.Domain.Verification;

namespace Praxis.Application.Desktop.Actions;

/// <summary>
/// The owner's view of work in progress: which model is doing what for how long, which jobs run,
/// what waits, and why nothing moves when something blocks it (a reached limit, failing calls, no
/// model for a role). Read-only.
/// </summary>
public sealed class ActivityActions(
    IActivityReader reader, ModelActivity activity, IVerificationRepository verification, IPolicyRepository policy, LocalPace pace, TimeProvider clock)
    : IDesktopActionGroup
{
    public IEnumerable<DesktopAction> Actions =>
    [
        DesktopAction.Of("activity", fields => Report(fields.IntegerOrDefault("recent", 40, 1, 200)))
    ];

    public ActivityResponse Report(int recent)
    {
        DateTimeOffset now = clock.GetUtcNow();
        ActivityFacts facts = reader.Read(now, recent);
        IReadOnlyList<TierAssignment> tiers = policy.ReadTiers();
        IReadOnlyList<VerificationProvider> connected = verification.ListProviders();
        Dictionary<Guid, string> files = facts.Running.GroupBy(job => job.ItemId).ToDictionary(group => group.Key, group => group.First().FileName);

        ModelCallInProgress[] calling = activity.Snapshot().Select(call => new ModelCallInProgress(call.Tier, call.ProviderId, call.Model, call.Purpose,
            call.ItemId is { } item && files.TryGetValue(item, out string? name) ? name : null, call.StartedUtc)).ToArray();
        ProviderActivity[] providers = connected.Select(provider =>
        {
            ProviderUsage? usage = facts.Providers.FirstOrDefault(candidate => candidate.ProviderId == provider.ProviderId);
            string[] roles = tiers.Where(tier => tier.ProviderId == provider.ProviderId).Select(tier => tier.Tier).ToArray();
            return new ProviderActivity(provider.ProviderId, provider.Enabled, provider.DailyLimit, usage?.CallsToday ?? 0, usage?.FailuresLastHour ?? 0,
                usage?.LastCallUtc, usage?.LastOutcome, roles);
        }).ToArray();

        return new ActivityResponse(calling, facts.Running, facts.Stages, providers, facts.RecentCalls, facts.Outcomes,
            Notices(facts, tiers, providers, now), pace.SecondsPerThousandCharacters);
    }

    private static List<ActivityNotice> Notices(
        ActivityFacts facts, IReadOnlyList<TierAssignment> tiers, ProviderActivity[] providers, DateTimeOffset now)
    {
        List<ActivityNotice> notices = [];
        foreach (string role in new[] { ModelTiers.Local, ModelTiers.Strong })
        {
            if (tiers.All(tier => tier.Tier != role))
            {
                notices.Add(new ActivityNotice("warning", $"No model is set for {Role(role)}, so files are only indexed for search."));
            }
        }

        foreach (ProviderActivity provider in providers.Where(provider => provider.Roles.Count > 0))
        {
            string roles = string.Join(" and ", provider.Roles.Select(Role));
            if (!provider.Enabled)
            {
                notices.Add(new ActivityNotice("warning", $"{Name(provider.ProviderId)} is turned off, so {roles} is paused."));
            }
            else if (provider.DailyLimit != VerificationProvider.Unlimited && provider.CallsToday >= provider.DailyLimit)
            {
                notices.Add(new ActivityNotice("warning",
                    $"{Name(provider.ProviderId)} used all {provider.DailyLimit} of today's calls, so {roles} waits. " +
                    "Files that needed it are read again after midnight UTC, or as soon as you raise the limit."));
            }

            if (provider is { FailuresLastHour: > 0, LastOutcome: { } last } && last != "completed")
            {
                notices.Add(new ActivityNotice("critical", $"{Name(provider.ProviderId)}'s latest calls failed: {Failure(last)}."));
            }
        }

        int stalled = facts.Running.Count(job => job.LeaseUntilUtc is { } until && until < now);
        if (stalled > 0)
        {
            notices.Add(new ActivityNotice("warning", $"{stalled} running job(s) stopped reporting; they are picked up again automatically."));
        }

        int waiting = facts.Outcomes.Where(outcome => outcome is { Status: "index_only", Reason: "tier_unavailable" or "model_quota_exhausted" })
            .Sum(outcome => outcome.Count);
        if (waiting > 0)
        {
            notices.Add(new ActivityNotice("info", $"{waiting} file(s) are searchable but wait for a model to be read."));
        }

        return notices;
    }

    private static string Role(string tier) => tier switch
    {
        ModelTiers.Local => "Triage",
        ModelTiers.Strong => "Reading",
        _ => "Checking"
    };

    private static string Name(string provider) => provider switch
    {
        VerificationProvider.ClaudeCode => "Claude Code",
        VerificationProvider.Codex => "Codex",
        VerificationProvider.Ollama => "Ollama",
        _ => provider
    };

    private static string Failure(string outcome) => outcome switch
    {
        "model_invalid_response" => "its answers could not be used",
        "model_timeout" => "it ran out of time",
        "model_quota_exhausted" => "the plan's usage limit was reached",
        "model_unauthenticated" => "it needs you to sign in again",
        "model_unavailable" => "it is not running or not reachable",
        _ => outcome.Replace('_', ' ')
    };
}
