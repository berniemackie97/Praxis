using Praxis.Domain.Agents;

namespace Praxis.Application.Agents;

/// <summary>Checks serialized HTTP request size before sending a model completion.</summary>
public interface IAgentBudgetedBackend : IAgentBackend
{
    Task<AgentTurn> CompleteWithinAsync(string systemPrompt, IReadOnlyList<AgentMessage> transcript,
        IReadOnlyList<AgentToolDefinition> tools, int maximumRequestBytes, CancellationToken cancellationToken);
}
