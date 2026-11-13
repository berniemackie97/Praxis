using Praxis.Application.Policy;
using Praxis.Domain.Policy;

namespace Praxis.Application.Agents;

/// <summary>
/// Per-run disclosure predicate for chat: may this source's content reach this backend? Decisions
/// come from the ADR 0043 policy for purpose <c>chat</c> and are cached for the run, so one turn
/// sees one consistent policy. Local backends always may.
/// </summary>
public sealed class DisclosureScope(DisclosureGate gate, string backendId, bool backendIsLocal)
{
    private readonly Dictionary<Guid, bool> decisions = [];
    private readonly Lock gateLock = new();

    public bool MayDisclose(Guid sourceId)
    {
        if (backendIsLocal)
        {
            return true;
        }

        lock (gateLock)
        {
            if (!decisions.TryGetValue(sourceId, out bool allowed))
            {
                allowed = gate.Decide(sourceId, backendId, false, DisclosurePurposes.Chat).Decision.Allowed;
                decisions[sourceId] = allowed;
            }

            return allowed;
        }
    }
}
