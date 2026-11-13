using Praxis.Domain.Models;
using Praxis.Domain.Policy;

namespace Praxis.Application.Policy;

/// <summary>
/// Runs one intake step up the model tiers by signal. The strong tier does the work; code
/// verifies; the strongest tier is asked only when the strong answer fails verification. When
/// remote processing is not allowed or not available, the source category's fallback chain
/// decides: a verified local answer, a time-limited hold for the owner, index-only, or skip.
/// No step waits forever and no tier or provider is substituted silently.
/// </summary>
public sealed class IntakeLadder(TieredModels models, IPolicyRepository policy, TimeProvider clock)
{
    /// <summary>Runs a step whose verification either accepts an answer or rejects it outright.</summary>
    public Task<LadderResult<T>> RunAsync<T>(
        TieredRequest request, Func<string, string, T?> verify, CancellationToken cancellationToken) where T : class =>
        RunAsync(request, (content, tier) => verify(content, tier) is { } value ? new LadderCheck<T>(value, true) : null, cancellationToken);

    /// <summary>
    /// Runs a step up the tiers. The strong tier answers; code checks; the strongest tier is asked
    /// when the check does not accept. If no tier is accepted, the best checked answer (the later
    /// tier's) is returned as partial. When remote use is not allowed or available, the source
    /// category's fallback chain decides.
    /// </summary>
    public async Task<LadderResult<T>> RunAsync<T>(
        TieredRequest request, Func<string, string, LadderCheck<T>?> check, CancellationToken cancellationToken) where T : class
    {
        TieredOutcome? blocked = null;
        bool ranUnverified = false;
        LadderResult<T>? partial = null;
        int input = 0;
        int output = 0;
        foreach (string tier in new[] { ModelTiers.Strong, ModelTiers.Strongest })
        {
            TieredOutcome outcome = await models.CompleteAsync(tier, request, cancellationToken);
            input += outcome.InputTokens;
            output += outcome.OutputTokens;
            if (!outcome.IsCompleted)
            {
                // Keep the first refusal: it is the one an owner decision (hold) can resolve.
                if (blocked is null || (blocked.Status != TieredOutcome.Disallowed && outcome.Status == TieredOutcome.Disallowed))
                {
                    blocked = outcome;
                }

                continue;
            }

            LadderCheck<T>? checkedAnswer = check(outcome.Content!, tier);
            if (checkedAnswer is { Acceptable: true })
            {
                return new LadderResult<T>(LadderResult<T>.Accepted, checkedAnswer.Value, tier, outcome.ProviderId, outcome.Identity, "verified", input, output);
            }

            ranUnverified = true;
            if (checkedAnswer is not null)
            {
                partial = new LadderResult<T>(LadderResult<T>.Partial, checkedAnswer.Value, tier, outcome.ProviderId, outcome.Identity, "partially_verified");
            }
        }

        if (partial is not null)
        {
            return partial with { InputTokens = input, OutputTokens = output };
        }

        LadderResult<T> result = ranUnverified
            ? new LadderResult<T>(LadderResult<T>.Unverified, null, null, null, null, "verification_failed")
            : await FallbackAsync(request, blocked, null, check, cancellationToken);
        return result with { InputTokens = result.InputTokens + input, OutputTokens = result.OutputTokens + output };
    }

    private async Task<LadderResult<T>> FallbackAsync<T>(
        TieredRequest request, TieredOutcome? blocked, string? unverifiedReason, Func<string, string, LadderCheck<T>?> check,
        CancellationToken cancellationToken) where T : class
    {
        DisclosurePolicy current = policy.ReadPolicy();
        string category = request.SourceId is { } source ? policy.ReadCategory(source)?.Category ?? request.KnownCategory ?? DataCategories.Unclassified
            : request.KnownCategory ?? DataCategories.Unclassified;
        foreach (string step in current.FallbacksFor(category))
        {
            switch (step)
            {
                case FallbackSteps.LocalIfVerified:
                    TieredOutcome local = await models.CompleteAsync(ModelTiers.Local, request, cancellationToken);
                    if (local.IsCompleted && check(local.Content!, ModelTiers.Local) is { Acceptable: true } value)
                    {
                        return new LadderResult<T>(LadderResult<T>.Accepted, value.Value, ModelTiers.Local, local.ProviderId, local.Identity, "local_verified",
                            local.InputTokens, local.OutputTokens);
                    }

                    break;
                case FallbackSteps.Hold when blocked is { Status: TieredOutcome.Disallowed, Decision.RequiresApproval: true, ProviderId: { } provider } &&
                                             request is { ItemId: { } item, Revision: { } revision, ProcessorKey: { } processor }:
                    DisclosureHold? hold = policy.ReadHold(item, revision, processor, provider);
                    if (hold is null)
                    {
                        policy.OpenHold(item, revision, processor, category, provider, request.Purpose, clock.GetUtcNow().Add(current.HoldDuration));
                        return new LadderResult<T>(LadderResult<T>.Held, null, null, provider, null, "awaiting_disclosure_approval");
                    }

                    if (hold.Status == DisclosureHold.Open)
                    {
                        return new LadderResult<T>(LadderResult<T>.Held, null, null, provider, null, "awaiting_disclosure_approval");
                    }

                    break;
                case FallbackSteps.IndexOnly:
                    return new LadderResult<T>(LadderResult<T>.IndexOnly, null, null, null, null, unverifiedReason ?? blocked?.FailureCode ?? "index_only");
                case FallbackSteps.Skip:
                    return new LadderResult<T>(LadderResult<T>.Skipped, null, null, null, null, "skipped_by_policy");
            }
        }

        return new LadderResult<T>(LadderResult<T>.IndexOnly, null, null, null, null, unverifiedReason ?? "index_only");
    }
}
