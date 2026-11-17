using System.Text;
using System.Text.Json.Nodes;
using Praxis.Domain.Agents;

namespace Praxis.Infrastructure.Agents;

/// <summary>Counts serialized request bytes without retaining request text or exposing private source bodies.</summary>
internal static class AgentHttpRequestFootprint
{
    public static AgentRequestFootprint Measure(string payload, JsonObject body, string system, IReadOnlyList<AgentMessage> transcript, int advertisedTools) =>
        new(
            Encoding.UTF8.GetByteCount(payload),
            Encoding.UTF8.GetByteCount(body["messages"]!.ToJsonString()),
            body["tools"] is { } tools ? Encoding.UTF8.GetByteCount(tools.ToJsonString()) : 0,
            Encoding.UTF8.GetByteCount(system),
            transcript.Where(message => message.Role == AgentRoles.Tool).Sum(message => Encoding.UTF8.GetByteCount(message.Content)),
            transcript.SelectMany(message => message.ToolCalls).Sum(call => Encoding.UTF8.GetByteCount(call.ArgumentsJson)),
            advertisedTools);
}
