using Praxis.Domain.Agents;

namespace Praxis.Application.Agents;

/// <summary>Opens a backend for a profile, resolving any Keychain-held key at call time.</summary>
public interface IAgentBackendFactory
{
    /// <summary>A turn-by-turn backend for HTTP kinds, or null for session (CLI) kinds.</summary>
    IAgentBackend? CreateTurnBackend(AgentBackendProfile profile);

    /// <summary>A session backend for CLI kinds, or null for HTTP kinds.</summary>
    ISessionAgentBackend? CreateSessionBackend(AgentBackendProfile profile);
}
