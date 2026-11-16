using System.Text.Json;
using Praxis.Application.Agents.Tools;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Capabilities;

public sealed class CapabilityRequestTool(CapabilityService capabilities) : IAgentTool
{
    public const string ToolName = "capability_request";

    public AgentToolDefinition Definition { get; } = new(ToolName,
        "Ask the owner to approve one exact action. terminal: exact shell command. browser-open: exact HTTPS URL. brew-install: exact Homebrew core formula. npm-install: one package@version. Discover commands first. The action does not happen until the owner approves it in Approvals.",
        ToolArguments.Schema("""
            {"type":"object","properties":{"operation":{"type":"string","enum":["terminal","browser-open","brew-install","npm-install"]},
              "target":{"type":"string"}},"required":["operation","target"]}
            """), ToolTiers.Propose, AuditArguments: false);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ToolArguments args = new(arguments);
        CapabilityRequest request = capabilities.Propose(context.ConversationId, context.OwnerText ?? "",
            args.RequireText("operation", 32), args.RequireText("target", 1_000));
        return Task.FromResult(AgentToolOutput.Of(new { request.Id, request.Operation, request.Status, reviewIn = "Approvals" }));
    }
}
