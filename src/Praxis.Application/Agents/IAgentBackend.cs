using Praxis.Domain.Agents;

namespace Praxis.Application.Agents;

/// <summary>
/// A tool-calling chat model Praxis drives turn by turn. Implementations translate the neutral
/// transcript and tool definitions to their provider's wire format and back.
/// </summary>
public interface IAgentBackend
{
    /// <summary>Validate chat support and report whether this model supports tools.</summary>
    ValueTask<bool> SupportsToolsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(true);

    Task<AgentTurn> CompleteAsync(string systemPrompt, IReadOnlyList<AgentMessage> transcript, IReadOnlyList<AgentToolDefinition> tools,
        CancellationToken cancellationToken);
}
