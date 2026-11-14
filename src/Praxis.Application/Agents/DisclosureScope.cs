using Praxis.Application.Policy;
using Praxis.Domain.Policy;

namespace Praxis.Application.Agents;

/// <summary>
/// Current disclosure predicate for chat. A previous tool call must never turn a revoked
/// permission into a grant for later calls in the same conversation turn.
/// </summary>
public sealed class DisclosureScope(DisclosureGate gate, string backendId, bool backendIsLocal)
{
    public bool MayDisclose(Guid sourceId) =>
        backendIsLocal || gate.Decide(sourceId, backendId, false, DisclosurePurposes.Chat).Decision.Allowed;
}
