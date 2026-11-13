using System.Text.Json;
using System.Text.Json.Nodes;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Mcp;

/// <summary>
/// The Model Context Protocol server side used by CLI agents: JSON-RPC 2.0 messages
/// <c>initialize</c>, <c>tools/list</c>, <c>tools/call</c> and <c>ping</c>. Tool calls are delegated to
/// the session's invoke function, so the registry's permission, disclosure and audit rules apply.
/// </summary>
public sealed class McpServerProtocol(IReadOnlyList<AgentToolDefinition> tools, Func<AgentToolCall, CancellationToken, Task<string>> invoke)
{
    public const string ProtocolVersion = "2025-06-18";
    private int calls;

    /// <summary>Handles one JSON-RPC line; returns the response line, or null for notifications.</summary>
    public async Task<string?> HandleAsync(string line, CancellationToken cancellationToken)
    {
        JsonNode? request;
        try
        {
            request = JsonNode.Parse(line);
        }
        catch (JsonException)
        {
            return Error(null, -32700, "Parse error");
        }

        JsonNode? id = request?["id"]?.DeepClone();
        string? method = request?["method"]?.GetValue<string>();
        if (method is null)
        {
            return id is null ? null : Error(id, -32600, "Invalid request");
        }

        if (id is null)
        {
            return null;
        }

        switch (method)
        {
            case "initialize":
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = ProtocolVersion,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                    ["serverInfo"] = new JsonObject { ["name"] = "praxis", ["version"] = "1" }
                });
            case "ping":
                return Result(id, new JsonObject());
            case "tools/list":
                return Result(id, new JsonObject
                {
                    ["tools"] = new JsonArray(tools.Select(tool => (JsonNode)new JsonObject
                    {
                        ["name"] = tool.Name,
                        ["description"] = tool.Description,
                        ["inputSchema"] = JsonNode.Parse(tool.InputSchema.GetRawText())
                    }).ToArray())
                });
            case "tools/call":
                string? name = request!["params"]?["name"]?.GetValue<string>();
                if (name is null || tools.All(tool => tool.Name != name))
                {
                    return Error(id, -32602, "Unknown tool");
                }

                string arguments = request["params"]?["arguments"]?.ToJsonString() ?? "{}";
                string content = await invoke(new AgentToolCall($"mcp-{Interlocked.Increment(ref calls)}", name, arguments), cancellationToken);
                bool failed = content.Contains("\"error\":", StringComparison.Ordinal) && content.Length < 600;
                return Result(id, new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = content }),
                    ["isError"] = failed
                });
            default:
                return Error(id, -32601, "Method not found");
        }
    }

    private static string Result(JsonNode id, JsonNode result) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result }.ToJsonString();

    private static string Error(JsonNode? id, int code, string message) =>
        new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } }.ToJsonString();
}
