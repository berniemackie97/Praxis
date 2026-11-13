using System.Text.Json;
using Praxis.Application.Policy;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Tools;

/// <summary>The owner's disclosure mode, tool permission, grants/denials, model tiers and pending confirmations.</summary>
public sealed class SettingsReadTool(PolicyService policy, IPolicyRepository repository) : IAgentTool
{
    public AgentToolDefinition Definition { get; } = new("settings_read",
        "Read what may leave this Mac and what you may change: disclosure mode, your tool permission, per-category grants/denials, fallback chains, model tiers, and changes waiting for owner confirmation. Use it to explain why something was withheld or denied.",
        ToolArguments.Schema("""{"type":"object","properties":{}}"""), ToolTiers.Read);

    public Task<AgentToolOutput> InvokeAsync(JsonElement arguments, AgentToolContext context, CancellationToken cancellationToken)
    {
        var current = policy.Current();
        return Task.FromResult(AgentToolOutput.Of(new
        {
            current.Mode,
            current.ToolPermission,
            holdHours = current.HoldDuration.TotalHours,
            thisBackend = context.BackendId,
            rules = current.Rules.Select(rule => new { rule.RuleId, rule.Category, rule.ProviderId, rule.Purpose, rule.Allow, rule.ExpiresUtc }),
            fallbacks = current.Fallbacks,
            tiers = repository.ReadTiers(),
            pending = policy.Pending().Select(change => new { change.ChangeId, change.RequestedBy, change.Rationale, change.RequestedUtc })
        }));
    }
}
