using Praxis.Domain.Models;
using Praxis.Domain.Policy;

namespace Praxis.Application.Policy;

/// <summary>
/// Durable disclosure policy, source categories, tier assignments, disclosure holds and the
/// model-invocation audit. Every policy change and disclosure is appended to the audit.
/// </summary>
public interface IPolicyRepository
{
    DisclosurePolicy ReadPolicy();

    /// <summary>Applies a validated change and audits who made it.</summary>
    void ApplyChange(PolicyChange change, string actor);

    Guid AddPendingChange(PolicyChange change, string actor, string rationale);

    IReadOnlyList<PendingPolicyChange> ReadPendingChanges();

    /// <summary>Confirms (applies) or rejects a pending change. Returns false when it is no longer pending.</summary>
    bool ResolvePendingChange(Guid changeId, bool confirm);

    SourceCategory? ReadCategory(Guid sourceId);

    /// <summary>Records a category. A model or hint never overrides an owner's category.</summary>
    void SaveCategory(SourceCategory category);

    IReadOnlyList<TierAssignment> ReadTiers();

    /// <summary>Assigns a provider to a tier, or clears the tier when <paramref name="providerId"/> is null.</summary>
    void SaveTier(string tier, string? providerId);

    /// <summary>The hold for this job and provider, if any (latest first).</summary>
    DisclosureHold? ReadHold(Guid itemId, int revision, string processorKey, string providerId);

    Guid OpenHold(Guid itemId, int revision, string processorKey, string category, string providerId, string purpose, DateTimeOffset deadline);

    IReadOnlyList<DisclosureHold> ReadHolds(bool openOnly, int limit);

    /// <summary>Records the owner's answer and requeues the waiting job. Returns null when the hold is not open.</summary>
    DisclosureHold? DecideHold(Guid holdId, string decision);

    /// <summary>Marks holds past their deadline expired and requeues their jobs. Returns how many expired.</summary>
    int ExpireHolds(DateTimeOffset now);

    void RecordInvocation(ModelInvocation invocation);

    int InvocationsToday(string providerId, DateTimeOffset now);

    IReadOnlyList<ModelInvocation> ReadInvocations(int limit);
}
