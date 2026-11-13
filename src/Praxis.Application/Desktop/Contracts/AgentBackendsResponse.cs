using Praxis.Domain.Agents;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>Configured chat backends.</summary>
public sealed record AgentBackendsResponse(IReadOnlyList<AgentBackendProfile> Backends);
