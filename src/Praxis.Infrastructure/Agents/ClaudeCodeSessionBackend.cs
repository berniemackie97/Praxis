using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Mcp;
using Praxis.Domain.Agents;
using Praxis.Infrastructure.Inference.Subscription;

namespace Praxis.Infrastructure.Agents;

/// <summary>
/// Claude Code (signed in with the owner's plan) as a session agent. Built-in tools are removed;
/// only the Praxis MCP tools are allowed, reached through the relay and executed by Praxis. Runs
/// in an empty private directory with no user settings, hooks, sessions or API-key variables.
/// </summary>
public sealed class ClaudeCodeSessionBackend(string executable, string model, AgentRelayCommand relay, string socketParent, string? effort = null)
    : ISessionAgentBackend
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(15);

    public async Task<AgentTurn> RunAsync(string systemPrompt, IReadOnlyList<AgentMessage> transcript, IReadOnlyList<AgentToolDefinition> tools,
        Func<AgentToolCall, CancellationToken, Task<string>> invoke, CancellationToken cancellationToken)
    {
        await using AgentToolSocketHost host = new(socketParent, new McpServerProtocol(tools, invoke));
        using CliWorkspace workspace = new();
        string config = Path.Combine(workspace.Path, "mcp.json");
        await File.WriteAllTextAsync(config, JsonSerializer.Serialize(new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["praxis"] = new { type = "stdio", command = relay.Executable, args = relay.LeadingArguments.Concat(["mcp-relay", host.SocketPath, host.Token]).ToArray() }
            }
        }), cancellationToken);
        string allowed = string.Join(",", tools.Select(tool => "mcp__praxis__" + tool.Name));
        CliResult result = await CliProcess.RunAsync(executable,
        [
            "-p", "--output-format", "json", "--mcp-config", config, "--strict-mcp-config", "--tools", "", "--allowedTools", allowed,
            "--permission-mode", "dontAsk", "--no-session-persistence",
            .. (model == "default" ? Array.Empty<string>() : ["--model", model]),
            .. (effort is null ? Array.Empty<string>() : ["--effort", effort]), "--system-prompt", systemPrompt
        ], CliTranscript.Render(transcript), workspace.Path, Deadline, cancellationToken);
        return Parse(result);
    }

    private static AgentTurn Parse(CliResult result)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(result.Stdout);
            JsonElement root = document.RootElement;
            string text = root.TryGetProperty("result", out JsonElement message) && message.ValueKind == JsonValueKind.String ? message.GetString()! : "";
            if (root.TryGetProperty("is_error", out JsonElement error) && error.ValueKind == JsonValueKind.True)
            {
                throw SubscriptionCli.Classify(text + " " + result.Stderr, "Claude Code reported an error.");
            }

            JsonElement usage = root.TryGetProperty("usage", out JsonElement used) ? used : default;
            return new AgentTurn(text, [], Tokens(usage, "input_tokens") + Tokens(usage, "cache_read_input_tokens"), Tokens(usage, "output_tokens"));
        }
        catch (JsonException)
        {
            throw SubscriptionCli.Classify(result.Stderr, "Claude Code returned malformed output.");
        }
    }

    private static int Tokens(JsonElement usage, string name) =>
        usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int tokens) ? tokens : 0;
}
