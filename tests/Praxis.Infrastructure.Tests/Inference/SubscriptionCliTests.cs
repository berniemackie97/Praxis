using System.Text.Json;
using Praxis.Application.Inference;
using Praxis.Infrastructure.Inference.Subscription;
using Xunit;

namespace Praxis.Infrastructure.Tests.Inference;

/// <summary>
/// Exercises the subscription adapters against stand-in executables, so the exact arguments,
/// environment and output handling are verified without a real provider call.
/// </summary>
public sealed class SubscriptionCliTests : IDisposable
{
    private static readonly JsonElement Schema = JsonDocument.Parse(
        """{"type":"object","additionalProperties":false,"required":["ok"],"properties":{"ok":{"type":"boolean"}}}""").RootElement;

    private readonly string folder = Path.Combine(Path.GetTempPath(), "praxis-cli-" + Guid.NewGuid().ToString("N"));

    public SubscriptionCliTests() => Directory.CreateDirectory(folder);

    [Fact]
    public async Task ClaudeCodeRunsWithoutToolsOrApiKeysAndReadsStructuredOutput()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var record = Path.Combine(folder, "record.txt");
        var claude = Script("claude", $$$"""
            printf '%s\n' "$@" > '{{{record}}}'
            printf 'stdin:' >> '{{{record}}}'; cat >> '{{{record}}}'
            printf '\nkey:%s\n' "${ANTHROPIC_API_KEY:-absent}" >> '{{{record}}}'
            pwd >> '{{{record}}}'
            echo '{"type":"result","subtype":"success","is_error":false,"duration_ms":1500,"structured_output":{"ok":true},"usage":{"input_tokens":10,"cache_read_input_tokens":5,"output_tokens":3}}'
            """);
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", "sk-test-must-not-leak");
        try
        {
            var result = await new ClaudeCodeChatModel(claude, "sonnet").CompleteAsync(Request(), CancellationToken.None);

            Assert.Equal("""{"ok":true}""", result.Content);
            Assert.Equal((15, 3, TimeSpan.FromSeconds(1.5)), (result.InputTokens, result.OutputTokens, result.Duration));
            var lines = File.ReadAllLines(record);
            Assert.Contains("--tools", lines);
            Assert.Equal("", lines[Array.IndexOf(lines, "--tools") + 1]);
            Assert.Equal("*", lines[Array.IndexOf(lines, "--disallowedTools") + 1]);
            Assert.Equal("sonnet", lines[Array.IndexOf(lines, "--model") + 1]);
            Assert.Equal("Verify strictly.", lines[Array.IndexOf(lines, "--system-prompt") + 1]);
            Assert.Contains("--no-session-persistence", lines);
            Assert.Contains("--strict-mcp-config", lines);
            Assert.DoesNotContain("--bare", lines);
            Assert.Contains(lines, line => line.StartsWith("stdin:Items: 1", StringComparison.Ordinal));
            Assert.Contains("key:absent", lines);
            var workingDirectory = lines[^1];
            Assert.Contains("praxis-provider-", workingDirectory, StringComparison.Ordinal);
            Assert.False(Directory.Exists(workingDirectory));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        }
    }

    [Theory]
    [InlineData("""{"subtype":"success","is_error":true,"result":"Failed to authenticate: OAuth session expired"}""", InferenceFailure.Unauthenticated)]
    [InlineData("""{"subtype":"success","is_error":true,"result":"You've hit your session limit · resets 4pm"}""", InferenceFailure.QuotaExhausted)]
    [InlineData("""{"subtype":"error_max_structured_output_retries","is_error":false}""", InferenceFailure.InvalidResponse)]
    [InlineData("""{"subtype":"success","is_error":false,"result":"ok"}""", InferenceFailure.InvalidResponse)]
    [InlineData("not json", InferenceFailure.InvalidResponse)]
    public void ClaudeCodeFailuresAreClassifiedWithoutProviderText(string stdout, InferenceFailure failure)
    {
        var exception = Assert.Throws<InferenceException>(() => ClaudeCodeChatModel.Parse(new CliResult(1, stdout, "")));
        Assert.Equal(failure, exception.Failure);
        Assert.DoesNotContain("resets", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodexUsesReadOnlyEphemeralExecWithSchemaAndReportsUsage()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var record = Path.Combine(folder, "record.txt");
        var codex = Script("codex", $$$"""
            printf '%s\n' "$@" > '{{{record}}}'
            printf 'key:%s\n' "${OPENAI_API_KEY:-absent}" >> '{{{record}}}'
            out=""; prev=""
            for arg in "$@"; do if [ "$prev" = "-o" ]; then out="$arg"; fi; prev="$arg"; done
            cat > /dev/null
            echo '{"ok":true}' > "$out"
            echo '{"type":"thread.started","thread_id":"t"}'
            echo 'diagnostic line'
            echo '{"type":"turn.completed","usage":{"input_tokens":120,"output_tokens":7,"reasoning_output_tokens":3}}'
            """);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", "sk-test-must-not-leak");
        try
        {
            var result = await new CodexChatModel(codex).CompleteAsync(Request(), CancellationToken.None);

            Assert.Equal("""{"ok":true}""", result.Content);
            Assert.Equal((120, 10), (result.InputTokens, result.OutputTokens));
            var lines = File.ReadAllLines(record);
            Assert.Equal("exec", lines[0]);
            Assert.Equal("read-only", lines[Array.IndexOf(lines, "--sandbox") + 1]);
            Assert.Contains("--ephemeral", lines);
            Assert.Contains("--ignore-user-config", lines);
            Assert.Contains("--output-schema", lines);
            Assert.DoesNotContain("-m", lines);
            Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", lines);
            Assert.Equal("-", lines[^2]);
            Assert.Equal("key:absent", lines[^1]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", null);
        }
    }

    [Fact]
    public void CodexFailureEventsAreDetected()
    {
        var (_, _, failure) = CodexChatModel.ReadEvents(
            """{"type":"turn.failed","error":{"message":"You've hit your usage limit"}}""");
        Assert.NotNull(failure);
        Assert.Equal(InferenceFailure.QuotaExhausted, SubscriptionCli.Classify(failure!, "x").Failure);
        Assert.Equal(InferenceFailure.InvalidResponse,
            SubscriptionCli.Classify("""{"type":"error","usage":{"input_tokens":1}}""", "x").Failure);
    }

    [Fact]
    public async Task HungProviderIsKilledAtItsDeadline()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var claude = Script("claude", "sleep 30");
        var started = DateTime.UtcNow;
        var exception = await Assert.ThrowsAsync<InferenceException>(() =>
            new ClaudeCodeChatModel(claude, "haiku", TimeSpan.FromSeconds(1)).CompleteAsync(Request(), CancellationToken.None));
        Assert.Equal(InferenceFailure.Timeout, exception.Failure);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("relative/claude", "sonnet")]
    [InlineData("/definitely/missing/claude", "sonnet")]
    public void InvalidExecutablesAreRejected(string executable, string model) =>
        Assert.Throws<ArgumentException>(() => new ClaudeCodeChatModel(executable, model));

    [Fact]
    public void ModelNamesCannotCarryArguments()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var claude = Script("claude", "exit 0");
        Assert.Throws<ArgumentException>(() => new ClaudeCodeChatModel(claude, "sonnet --dangerously-skip-permissions"));
        Assert.Throws<ArgumentException>(() => new CodexChatModel(claude, "gpt 5"));
    }

    private static ChatRequest Request() => new(
        [new ChatMessage(ChatRole.System, "Verify strictly."), new ChatMessage(ChatRole.User, "Items: 1")],
        Schema, MaxOutputTokens: 256, ContextTokens: 4096);

    private string Script(string name, string body)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
