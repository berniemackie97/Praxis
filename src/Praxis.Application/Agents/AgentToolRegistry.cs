using System.Diagnostics;
using System.Text.Json;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents;

/// <summary>
/// The single registry every backend uses — Praxis's own loop, CLI agents over MCP and future
/// external clients. It parses untrusted arguments, enforces the permission tier outside the
/// model, bounds result size, turns validation failures into structured errors the model can
/// correct, and audits every call.
/// </summary>
public sealed class AgentToolRegistry(IEnumerable<IAgentTool> tools, IAgentConversationRepository audit, TimeProvider clock)
{
    public const int MaximumResultCharacters = 24_000;

    private readonly IReadOnlyDictionary<string, IAgentTool> byName = tools.ToDictionary(tool => tool.Definition.Name, StringComparer.Ordinal);

    public IReadOnlyList<AgentToolDefinition> Definitions => byName.Values.Select(tool => tool.Definition).OrderBy(definition => definition.Name, StringComparer.Ordinal).ToArray();

    public async Task<AgentToolOutput> InvokeAsync(AgentToolCall call, AgentToolContext context, CancellationToken cancellationToken, AgentReadProgress? reads = null)
    {
        DateTimeOffset started = clock.GetUtcNow();
        long timestamp = Stopwatch.GetTimestamp();
        AgentToolOutput output;
        string outcome;
        string tier = "unknown";
        bool auditArguments = false;
        if (!byName.TryGetValue(call.Name, out IAgentTool? tool))
        {
            (output, outcome) = (AgentToolOutput.Error("unknown_tool", $"No tool named '{call.Name}'. Use one of: {string.Join(", ", byName.Keys.Order())}."), "unknown_tool");
        }
        else if (tool.Definition.LocalOnly && !context.BackendIsLocal)
        {
            tier = tool.Definition.Tier;
            (output, outcome) = (AgentToolOutput.Error("local_only", "Use a local model for this tool."), "permission_denied");
        }
        else if (!ToolTiers.Permits(context.ToolPermission, tier = tool.Definition.Tier))
        {
            (output, outcome) = (AgentToolOutput.Error("permission_denied",
                tier == ToolTiers.System
                    ? "System actions are disabled in Praxis. Tell the owner what you would do instead."
                    : $"The owner's tool permission is '{context.ToolPermission}', which does not allow {tier} tools. Explain what you would change and ask the owner to allow edits, or use a propose tool."),
                "permission_denied");
        }
        else
        {
            auditArguments = tool.Definition.AuditArguments;
            if (reads is not null && reads.TryRepeat(call, context.MayDisclose, out string previous))
            {
                output = AgentToolOutput.Of(new
                {
                    error = "already_read",
                    previousCallId = previous,
                    message = "This exact read already completed. Use the earlier evidence, or change the query or offset."
                });
                outcome = "duplicate_read";
            }
            else
            {
                (output, outcome) = await RunAsync(tool, call, context, cancellationToken);
            }
        }

        if (output.Content.Length > MaximumResultCharacters)
        {
            // Keep the result valid JSON: wrap the leading part instead of cutting mid-structure.
            output = output with { Content = Tools.ToolJson.Serialize(new { truncated = true, partial = output.Content[..(MaximumResultCharacters - 200)] }) };
            outcome = outcome == "ok" ? "truncated" : outcome;
        }

        if (outcome != "duplicate_read")
        {
            reads?.Record(call, output, outcome == "ok");
        }

        audit.RecordToolCall(new AgentToolCallRecord(context.ConversationId, call.CallId, call.Name, tier, auditArguments ? Bound(call.ArgumentsJson) : "{}", outcome,
            output.Content.Length, output.Citations.Count, output.Withheld.Sum(notice => notice.Count), started, Stopwatch.GetElapsedTime(timestamp)));
        return output;
    }

    private static async Task<(AgentToolOutput, string)> RunAsync(IAgentTool tool, AgentToolCall call, AgentToolContext context, CancellationToken cancellationToken)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson, new JsonDocumentOptions { MaxDepth = 16 });
        }
        catch (JsonException)
        {
            return (AgentToolOutput.Error("invalid_arguments", "Arguments must be a JSON object matching the tool's schema."), "invalid_arguments");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (AgentToolOutput.Error("invalid_arguments", "Arguments must be a JSON object."), "invalid_arguments");
            }

            try
            {
                return (await tool.InvokeAsync(document.RootElement, context, cancellationToken), "ok");
            }
            catch (ArgumentException exception)
            {
                return (AgentToolOutput.Error("invalid_arguments", exception.Message), "invalid_arguments");
            }
            catch (InvalidOperationException exception)
            {
                return (AgentToolOutput.Error("unavailable", exception.Message), "unavailable");
            }
        }
    }

    private static string Bound(string arguments) => arguments.Length <= 4_000 ? arguments : arguments[..4_000];
}
