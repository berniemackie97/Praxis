using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Mcp;
using Praxis.Application.Inference;
using Praxis.Domain.Agents;
using Praxis.Infrastructure.Inference.Subscription;

namespace Praxis.Infrastructure.Agents;

/// <summary>
/// Codex (signed in with the owner's ChatGPT plan) as a session agent: read-only sandbox,
/// ephemeral, no user config or rules, with the Praxis relay as its only MCP server.
/// </summary>
public sealed class CodexSessionBackend(string executable, string model, AgentRelayCommand relay, string socketParent, string? effort = null)
    : ISessionAgentBackend
{
    // These reads are either source-disclosure checked or purely computational.
    private static readonly HashSet<string> NoninteractiveReads = new(StringComparer.Ordinal)
    {
        "knowledge_maintenance", "knowledge_search", "knowledge_open", "knowledge_entity", "knowledge_related", "knowledge_project", "knowledge_timeline",
        "data_describe", "data_query", "compute_evaluate", "skill_search", "skill_open", "media_find", "capability_discover", "capability_result"
    };

    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(15);

    public async Task<AgentTurn> RunAsync(string systemPrompt, IReadOnlyList<AgentMessage> transcript, IReadOnlyList<AgentToolDefinition> tools,
        Func<AgentToolCall, CancellationToken, Task<string>> invoke, CancellationToken cancellationToken)
    {
        // The relay exposes read operations and owner-reviewed proposals. Codex cannot
        // approve any action itself, even when its CLI does not need an extra relay prompt.
        AgentToolDefinition[] exposed = ExposedTools(tools);
        await using AgentToolSocketHost? host = exposed.Length == 0 ? null : new(socketParent, new McpServerProtocol(exposed, invoke));
        using CliWorkspace workspace = new();
        string answer = Path.Combine(workspace.Path, "answer.txt");
        string[] serverOptions = host is null ? [] :
        [
            "-c", "mcp_servers.praxis.default_tools_approval_mode=\"approve\"",
            "-c", $"mcp_servers.praxis.command={JsonSerializer.Serialize(relay.Executable)}",
            "-c", $"mcp_servers.praxis.args={JsonSerializer.Serialize(relay.LeadingArguments.Concat(["mcp-relay", host.SocketPath, host.Token]).ToArray())}"
        ];
        CliResult result = await CliProcess.RunAsync(executable,
        [
            "exec", "--json", "--sandbox", "read-only", "--ephemeral", "--ignore-user-config", "--ignore-rules", "--skip-git-repo-check",
            "--disable", "shell_tool", "--disable", "view_image", "--disable", "apps", "--disable", "multi_agent",
            "-c", "web_search=\"disabled\"",
            .. serverOptions,
            .. (model == "default" ? Array.Empty<string>() : ["-m", model]), .. (effort is null ? Array.Empty<string>() : ["-c", $"model_reasoning_effort=\"{effort}\""]), "-o", answer, "-"
        ], systemPrompt + "\n\n" + CliTranscript.Render(transcript), workspace.Path, Deadline, cancellationToken);
        var (input, output, cached, failure) = ReadEvents(result.Stdout);
        if (result.ExitCode != 0 || failure is not null || !File.Exists(answer))
        {
            throw ClassifyFailure(failure, result.Stderr);
        }

        string text = (await File.ReadAllTextAsync(answer, cancellationToken)).Trim();
        if (text.Length == 0)
        {
            throw new InferenceException(InferenceFailure.InvalidResponse, "Codex returned an empty answer.");
        }

        return new AgentTurn(text, [], input, output, CachedInputTokens: cached);
    }

    internal static AgentToolDefinition[] ExposedTools(IReadOnlyList<AgentToolDefinition> tools) => tools
        .Where(tool => !tool.LocalOnly && ((tool.Tier == ToolTiers.Read && NoninteractiveReads.Contains(tool.Name)) ||
            (tool.Tier == ToolTiers.Propose && tool.Name is "capability_request" or "knowledge_propose"))).ToArray();

    internal static (int Input, int Output, int CachedInput, string? Failure) ReadEvents(string stdout)
    {
        int input = 0;
        int output = 0;
        int cached = 0;
        string? failure = null;
        using StringReader lines = new(stdout);
        while (lines.ReadLine() is { } line)
        {
            try
            {
                using JsonDocument parsed = JsonDocument.Parse(line);
                JsonElement root = parsed.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out JsonElement type) ||
                    type.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (type.GetString() == "turn.completed" && root.TryGetProperty("usage", out JsonElement usage))
                {
                    input = Tokens(usage, "input_tokens");
                    output = Tokens(usage, "output_tokens");
                    cached = Math.Min(input, Tokens(usage, "cached_input_tokens"));
                }
                else if (type.GetString() == "turn.failed")
                {
                    failure = root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.Object &&
                        error.TryGetProperty("message", out JsonElement message) && message.ValueKind == JsonValueKind.String
                        ? message.GetString() : "";
                }
            }
            catch (JsonException)
            {
                // Unknown telemetry must not invalidate an otherwise successful answer.
            }
        }

        return (input, output, cached, failure);
    }

    private static int Tokens(JsonElement usage, string name) =>
        usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int tokens) ? Math.Max(0, tokens) : 0;

    internal static InferenceException ClassifyFailure(string? failure, string stderr)
    {
        string diagnostic = failure + "\n" + stderr;
        if (diagnostic.Contains("approval required", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("approval is required", StringComparison.OrdinalIgnoreCase) ||
            diagnostic.Contains("requires approval", StringComparison.OrdinalIgnoreCase))
        {
            return new InferenceException(InferenceFailure.ApprovalRequired, "Codex required interactive approval.");
        }

        return SubscriptionCli.Classify(diagnostic, "Codex did not produce an answer.");
    }
}
