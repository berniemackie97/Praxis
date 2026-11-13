using Praxis.Domain.Agents;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>The agent's knowledge proposals.</summary>
public sealed record AgentProposalsResponse(IReadOnlyList<AgentProposal> Proposals);
