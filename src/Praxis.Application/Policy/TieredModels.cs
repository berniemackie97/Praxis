using Praxis.Application.Inference;
using Praxis.Application.Verification;
using Praxis.Domain.Models;
using Praxis.Domain.Policy;
using Praxis.Domain.Verification;

namespace Praxis.Application.Policy;

/// <summary>
/// Calls a tier on behalf of a source: resolves the tier's provider, enforces its daily allowance,
/// asks the disclosure gate (honouring a one-time owner approval), invokes the model and records
/// the invocation. Never substitutes another provider or tier: escalation and fallback are the
/// caller's explicit choice.
/// </summary>
public sealed class TieredModels(
    IPolicyRepository policy, IVerificationRepository providers, IVerifierModelFactory factory, DisclosureGate gate, TimeProvider clock)
{
    public bool IsConfigured(string tier) => Resolve(tier) is not null;

    public async Task<TieredOutcome> CompleteAsync(string tier, TieredRequest request, CancellationToken cancellationToken)
    {
        if (!ModelTiers.All.Contains(tier, StringComparer.Ordinal) || !DisclosurePurposes.All.Contains(request.Purpose, StringComparer.Ordinal))
        {
            throw new ArgumentException("The tier or purpose is not recognized.");
        }

        VerificationProvider? provider = Resolve(tier);
        if (provider is null)
        {
            return new TieredOutcome(TieredOutcome.NotConfigured, null, null, null, null, "tier_unavailable");
        }

        (DisclosureDecision decision, string category) = gate.Decide(request.SourceId, provider.ProviderId, provider.IsLocal, request.Purpose, request.KnownCategory);
        if (!decision.Allowed && !ApprovedOnce(request, provider))
        {
            return new TieredOutcome(TieredOutcome.Disallowed, null, null, provider.ProviderId, decision, decision.Reason);
        }

        DateTimeOffset started = clock.GetUtcNow();
        try
        {
            using ModelConnection connection = factory.Create(provider);
            ModelIdentity identity = await connection.Model.DescribeAsync(cancellationToken);
            ChatResult result = await connection.Model.CompleteAsync(request.Request, cancellationToken);
            Record(tier, provider, request, category, decision, identity.Model, result.InputTokens, result.OutputTokens, "completed", started);
            return new TieredOutcome(TieredOutcome.Completed, result.Content, identity, provider.ProviderId, decision, null);
        }
        catch (InferenceException exception)
        {
            Record(tier, provider, request, category, decision, provider.Model, 0, 0, exception.Code, started);
            return new TieredOutcome(TieredOutcome.Failed, null, null, provider.ProviderId, decision, exception.Code);
        }
    }

    private VerificationProvider? Resolve(string tier)
    {
        string? providerId = policy.ReadTiers().FirstOrDefault(assignment => assignment.Tier == tier)?.ProviderId;
        VerificationProvider? provider = providerId is null ? null : providers.ListProviders().FirstOrDefault(candidate => candidate.ProviderId == providerId);
        return provider is { Enabled: true } && policy.InvocationsToday(provider.ProviderId, clock.GetUtcNow()) < provider.DailyLimit ? provider : null;
    }

    private bool ApprovedOnce(TieredRequest request, VerificationProvider provider) =>
        request is { ItemId: { } item, Revision: { } revision, ProcessorKey: { } processor } &&
        policy.ReadHold(item, revision, processor, provider.ProviderId) is { Status: DisclosureHold.ApprovedOnce };

    private void Record(string tier, VerificationProvider provider, TieredRequest request, string category, DisclosureDecision decision,
        string? model, int input, int output, string outcome, DateTimeOffset started) =>
        policy.RecordInvocation(new ModelInvocation(tier, provider.ProviderId, !provider.IsLocal, request.Purpose, category, decision.Reason,
            request.ItemId, request.Revision, request.ProcessorKey, model, input, output, outcome, started));
}
