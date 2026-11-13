using Praxis.Domain.Models;
using Praxis.Domain.Policy;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>The disclosure policy, tier assignments and changes awaiting owner confirmation.</summary>
public sealed record PolicyResponse(DisclosurePolicy Policy, IReadOnlyList<TierAssignment> Tiers, IReadOnlyList<PendingPolicyChange> Pending);
