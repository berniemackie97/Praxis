using Praxis.Domain.Policy;

namespace Praxis.Application.Policy;

/// <summary>
/// The owner can change policy directly in the first-party UI. An agent request is always queued
/// for the owner to confirm or reject. Nothing sent in chat or emitted by a model can confirm it.
/// </summary>
public sealed class PolicyService(IPolicyRepository repository)
{
    public DisclosurePolicy Current() => repository.ReadPolicy();

    public PolicyChangeOutcome Request(PolicyChange change, string actor, string rationale = "")
    {
        ArgumentNullException.ThrowIfNull(change);
        change.Validate();
        if (actor is not (PolicyActors.Owner or PolicyActors.Agent))
        {
            throw new ArgumentException("The policy actor is not recognized.", nameof(actor));
        }

        if (rationale.Length > 500)
        {
            throw new ArgumentException("The rationale is too long.", nameof(rationale));
        }

        if (actor == PolicyActors.Owner)
        {
            repository.ApplyChange(change, actor);
            return new PolicyChangeOutcome(true, null);
        }

        return new PolicyChangeOutcome(false, repository.AddPendingChange(change, actor, rationale));
    }

    /// <summary>Owner-only: exposed to the first-party UI, never registered as an agent tool.</summary>
    public bool ResolvePending(Guid changeId, bool confirm) => repository.ResolvePendingChange(changeId, confirm);

    public IReadOnlyList<PendingPolicyChange> Pending() => repository.ReadPendingChanges();
}
