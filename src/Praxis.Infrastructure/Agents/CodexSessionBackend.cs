using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Mcp;
using Praxis.Domain.Agents;
using Praxis.Infrastructure.Inference.Subscription;

namespace Praxis.Infrastructure.Agents;

/// <summary>
/// Codex (signed in with the owner's ChatGPT plan) as a session agent: read-only sandbox,
/// ephemeral, no user config or rules, with the Praxis relay as its only MCP server.
/// </summary>
public sealed class CodexSessionBackend(string executable, string model, AgentRelayCommand relay, string socketParent) : ISessionAgentBackend
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(15);

    public async Task<AgentTurn> RunAsync(string systemPrompt, IReadOnlyList<AgentMessage> transcript, IReadOnlyList<AgentToolDefinition> tools,
        Func<AgentToolCall, CancellationToken, Task<string>> invoke, CancellationToken cancellationToken)
    {
        await using AgentToolSocketHost host = new(socketParent, new McpServerProtocol(tools, invoke));
        using CliWorkspace workspace = new();
        string answer = Path.Combine(workspace.Path, "answer.txt");
        string arguments = JsonSerializer.Serialize(relay.LeadingArguments.Concat(["mcp-relay", host.SocketPath, host.Token]).ToArray());
        CliResult result = await CliProcess.RunAsync(executable,
        [
            "exec", "--sandbox", "read-only", "--ephemeral", "--ignore-user-config", "--ignore-rules", "--skip-git-repo-check",
            "-c", $"mcp_servers.praxis.command={JsonSerializer.Serialize(relay.Executable)}", "-c", $"mcp_servers.praxis.args={arguments}",
            "-m", model, "-o", answer, "-"
        ], systemPrompt + "\n\n" + CliTranscript.Render(transcript), workspace.Path, Deadline, cancellationToken);
        if (result.ExitCode != 0 || !File.Exists(answer))
        {
            throw SubscriptionCli.Classify(result.Stderr, "Codex did not produce an answer.");
        }

        return new AgentTurn((await File.ReadAllTextAsync(answer, cancellationToken)).Trim(), [], 0, 0);
    }
}
