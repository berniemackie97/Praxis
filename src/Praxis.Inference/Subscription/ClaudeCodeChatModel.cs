using System.Text.Json;

namespace Praxis.Inference;

/// <summary>
/// Structured completions through the owner's own Claude Code installation in headless print
/// mode, using its Claude subscription sign-in. All tools, MCP servers and session persistence
/// are disabled; the call runs in an empty private directory; API-key variables are removed so
/// it cannot bill the API. The result's <c>structured_output</c> must be present and
/// <c>is_error</c> false, whatever <c>subtype</c> says.
/// </summary>
public sealed class ClaudeCodeChatModel : IChatModel
{
    public const string ProviderName = "claude-code";
    private readonly string executable;
    private readonly string model;
    private readonly TimeSpan timeout;

    public ClaudeCodeChatModel(string executable, string model, TimeSpan? timeout = null)
    {
        this.executable = SubscriptionCli.ValidateExecutable(executable);
        this.model = SubscriptionCli.ValidateModel(model);
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
            throw new InferenceException(InferenceFailure.Unavailable, "Claude Code did not report its version.");
        }

        return new ModelIdentity(ProviderName, model, version, "claude", null, null);
    }

    public async Task<ChatResult> CompleteAsync(ChatRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        using var workspace = new CliWorkspace();
        var result = await CliProcess.RunAsync(executable,
        [
            "-p", "Answer the request provided on standard input. Respond only through the required schema.",
            "--output-format", "json",
            "--json-schema", request.ResponseSchema.GetRawText(),
            "--tools", "",
            "--disallowedTools", "*",
            "--permission-mode", "dontAsk",
            "--no-session-persistence",
            "--strict-mcp-config",
            "--model", model,
            "--system-prompt", SubscriptionCli.System(request)
        ], SubscriptionCli.User(request), workspace.Path, timeout, cancellationToken);
        return Parse(result);
    }

    internal static ChatResult Parse(CliResult result)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(result.Stdout);
        }
        catch (JsonException exception)
        {
            var classified = SubscriptionCli.Classify(result.Stderr, "Claude Code returned malformed output.");
            throw classified.Failure != InferenceFailure.InvalidResponse
                ? classified
                : new InferenceException(InferenceFailure.InvalidResponse, "Claude Code returned malformed output.", exception);
        }

        using (document)
        {
            var root = document.RootElement;
            var failed = root.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True;
            var subtype = root.TryGetProperty("subtype", out var kind) ? kind.GetString() : null;
            if (failed || subtype != "success" ||
                !root.TryGetProperty("structured_output", out var output) || output.ValueKind != JsonValueKind.Object)
            {
                var text = root.TryGetProperty("result", out var message) && message.ValueKind == JsonValueKind.String
                    ? message.GetString() ?? "" : "";
                throw SubscriptionCli.Classify(text + " " + result.Stderr, "Claude Code did not return structured output.");
            }

            var usage = root.TryGetProperty("usage", out var used) ? used : default;
            return new ChatResult(output.GetRawText(),
                Tokens(usage, "input_tokens") + Tokens(usage, "cache_read_input_tokens") +
                Tokens(usage, "cache_creation_input_tokens"),
                Tokens(usage, "output_tokens"),
                TimeSpan.FromMilliseconds(root.TryGetProperty("duration_ms", out var ms) &&
                    ms.TryGetInt64(out var duration) ? duration : 0));
        }
    }

    private static int Tokens(JsonElement usage, string property) =>
        usage.ValueKind == JsonValueKind.Object && usage.TryGetProperty(property, out var value) &&
        value.TryGetInt32(out var tokens) && tokens >= 0 ? tokens : 0;
}
