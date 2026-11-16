using System.Text.Json;
using Praxis.Application.Agents.Tools;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Capabilities;

public sealed class CapabilityResultTool(CapabilityService capabilities) : IAgentTool
{
    public const string ToolName = "capability_result";

    public AgentToolDefinition Definition { get; } = new(ToolName,
        "Read the status of a previously requested action from this chat by its request ID. Output stays on this Mac and can be read by a local model.",
        ToolArguments.Schema("""{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}"""), ToolTiers.Read);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guid id = new ToolArguments(arguments).RequireGuid("id");
        if (capabilities.Read(id) is not { } request || request.ConversationId != context.ConversationId)
        {
            return Task.FromResult(AgentToolOutput.Error("unknown_action", "No action with that ID belongs to this chat."));
        }

        return Task.FromResult(AgentToolOutput.Of(new
        {
            request.Id,
            request.Status,
            request.Operation,
            detail = context.BackendIsLocal ? request.Detail : null
        }));
    }
}
