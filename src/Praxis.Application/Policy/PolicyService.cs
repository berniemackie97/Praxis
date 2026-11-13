using Praxis.Domain.Policy;

namespace Praxis.Application.Policy;

/// <summary>
/// The only way policy changes. The owner acting in the first-party UI applies any change. Any
/// other requester (the agent, on behalf of a chat message) applies narrowing changes at once;
/// widening changes wait for the owner to confirm in the UI. Nothing a model emits can confirm.
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

        if (actor == PolicyActors.Owner || !change.Widens(repository.ReadPolicy()))
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
