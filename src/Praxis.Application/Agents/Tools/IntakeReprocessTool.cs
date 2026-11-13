using System.Text.Json;
using Praxis.Application.Processing;
using Praxis.Domain.Agents;
using Praxis.Domain.Processing;

namespace Praxis.Application.Agents.Tools;

/// <summary>Re-runs an intake step for one item (for example after the owner granted disclosure or configured a stronger tier). Policy still applies.</summary>
public sealed class IntakeReprocessTool(IProcessingQueue processing) : IAgentTool
{
    private static readonly IReadOnlyList<string> Steps =
        [ProcessorKeys.IntakeUnderstand, ProcessorKeys.IntakeTriage, ProcessorKeys.SemanticExtraction, ProcessorKeys.SourceText];

    public AgentToolDefinition Definition { get; } = new("intake_reprocess",
        $"Queue an intake step to run again for one item (itemId and revision from intake_status or search). step: {string.Join(" | ", Steps)}. The owner's disclosure policy still governs where it runs.",
        ToolArguments.Schema("""{"type":"object","properties":{"itemId":{"type":"string"},"revision":{"type":"integer","minimum":1},"step":{"type":"string"}},"required":["itemId","revision","step"]}"""),
        ToolTiers.Propose);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        ToolArguments args = new(arguments);
        bool queued = processing.Requeue(args.RequireGuid("itemId"), args.Integer("revision", 1, 1, int.MaxValue), args.Choice("step", Steps, ProcessorKeys.IntakeUnderstand));
        return Task.FromResult(AgentToolOutput.Of(new { queued, note = queued ? null : "Nothing to requeue: the step is already queued or running, or the item is not current." }));
    }
}
