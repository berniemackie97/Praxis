using System.Diagnostics;
using System.Text.Json;
using Praxis.Application.Inference;
using Praxis.Domain.Models;

namespace Praxis.Infrastructure.Inference.Subscription;

/// <summary>
/// Structured completions through the owner's own Codex CLI (<c>codex exec</c>) using its
/// ChatGPT plan sign-in: read-only sandbox, ephemeral session, user configuration and rules
/// ignored, an empty private working directory, and API-key variables removed. The final
/// message must conform to the schema; usage comes from the JSON event stream.
/// </summary>
public sealed class CodexChatModel : IChatModel
{
    private const string Instructions =
        "You answer one request from a private knowledge base. Return only JSON that matches the given schema. Use no tools.\n";

    // Agent features Praxis never uses; each adds tool definitions to every request.
    private static readonly string[] UnusedFeatures =
    [
        "apps", "browser_use", "browser_use_external", "code_mode_host", "computer_use", "goals", "hooks", "image_generation",
        "in_app_browser", "memories", "multi_agent", "plugins", "shell_snapshot", "shell_tool", "skill_search", "sleep_tool",
        "steer", "tool_suggest", "unified_exec", "view_image", "workspace_dependencies", "worktrees"
    ];

    public const string ProviderName = "codex";
    private readonly string executable;
    private readonly string? model;
    private readonly string? effort;
    private readonly TimeSpan timeout;

    /// <param name="executable">Absolute path of the owner's Codex CLI.</param>
    /// <param name="model">A specific model, or null for the plan's default Codex model.</param>
    /// <param name="timeout">Deadline for one call.</param>
    /// <param name="effort">Reasoning effort from the model's catalog, or null for the model default.</param>
    public CodexChatModel(string executable, string? model = null, TimeSpan? timeout = null, string? effort = null)
    {
        this.executable = SubscriptionCli.ValidateExecutable(executable);
        this.model = model is null ? null : SubscriptionCli.ValidateModel(model);
        this.effort = SubscriptionCli.ValidateEffort(effort);
        this.timeout = timeout ?? SubscriptionCli.DefaultTimeout;
    }

    public async Task<ModelIdentity> DescribeAsync(CancellationToken cancellationToken)
    {
        using var workspace = new CliWorkspace();
        var result = await CliProcess.RunAsync(executable, ["--version"], null, workspace.Path,
            TimeSpan.FromSeconds(30), cancellationToken);
        var version = result.Stdout.Trim();
        if (result.ExitCode != 0 || version.Length is < 1 or > 100)
        {
            throw new InferenceException(InferenceFailure.Unavailable, "Codex did not report its version.");
        }

        return new ModelIdentity(ProviderName, model ?? "default", version, "openai", null, null);
    }

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        using var workspace = new CliWorkspace();
        var schemaPath = Path.Combine(workspace.Path, "schema.json");
        var answerPath = Path.Combine(workspace.Path, "answer.json");
        // Codex enforces OpenAI's strict structured-output rules.
        await File.WriteAllTextAsync(schemaPath, StrictSchema.Convert(request.ResponseSchema), cancellationToken);
        // Codex's own agent instructions and tool definitions cost about 9,000 input tokens per call;
        // Praxis needs neither, so it replaces the instructions and turns the tools off.
        var instructionsPath = Path.Combine(workspace.Path, "instructions.md");
        await File.WriteAllTextAsync(instructionsPath, Instructions, cancellationToken);
        List<string> arguments =
        [
            "exec", "--sandbox", "read-only", "--ephemeral", "--ignore-user-config", "--ignore-rules",
            "--skip-git-repo-check", "-C", workspace.Path, "--output-schema", schemaPath,
            "-o", answerPath, "--json",
            "-c", $"model_instructions_file=\"{instructionsPath}\"",
            "-c", "mcp_servers={}", "-c", "web_search=\"disabled\"",
            .. UnusedFeatures.SelectMany(feature => new[] { "-c", $"features.{feature}=false" })
        ];
        if (model is not null)
        {
            arguments.AddRange(["-m", model]);
        }

        if (effort is not null)
        {
            arguments.AddRange(["-c", $"model_reasoning_effort=\"{effort}\""]);
        }

        arguments.Add("-");
        var prompt = "Instructions:\n" + SubscriptionCli.System(request) +
            "\n\nRequest:\n" + SubscriptionCli.User(request);
        var started = Stopwatch.StartNew();
        var result = await CliProcess.RunAsync(executable, arguments, prompt, workspace.Path, timeout, cancellationToken);
        var (inputTokens, outputTokens, failure) = ReadEvents(result.Stdout);
        if (result.ExitCode != 0 || failure is not null || !File.Exists(answerPath))
        {
            throw SubscriptionCli.Classify((failure ?? "") + " " + result.Stderr, "Codex did not return an answer.");
        }

        if (new FileInfo(answerPath).Length > CliProcess.MaximumOutputBytes)
        {
            throw new InferenceException(InferenceFailure.InvalidResponse,
                "The verifier returned more structured output than allowed.");
        }

        var answer = await File.ReadAllTextAsync(answerPath, cancellationToken);
        return new ChatResult(answer.Trim(), inputTokens, outputTokens, started.Elapsed);
    }

    /// <summary>Token usage from <c>turn.completed</c> and the text of any failure event.</summary>
    internal static (int Input, int Output, string? Failure) ReadEvents(string jsonl)
    {
        int input = 0, output = 0;
        string? failure = null;
        foreach (var line in jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue; // Non-event diagnostic lines are not part of the event protocol.
            }

            using (document)
            {
                var root = document.RootElement;
                var type = root.TryGetProperty("type", out var value) ? value.GetString() : null;
                if (type == "turn.completed" && root.TryGetProperty("usage", out var usage))
                {
                    input += Number(usage, "input_tokens");
                    output += Number(usage, "output_tokens") + Number(usage, "reasoning_output_tokens");
                }
                else if (type is "turn.failed" or "error")
                {
                    failure = root.GetRawText();
                }
            }
        }

        return (input, output, failure);
    }

    private static int Number(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number) && number >= 0 ? number : 0;
}
