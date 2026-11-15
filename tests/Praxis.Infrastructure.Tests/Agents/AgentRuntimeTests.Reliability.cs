using System.Text.Json;
using Praxis.Application.Agents;
using Praxis.Application.Agents.Runtime;
using Praxis.Domain.Agents;
using Praxis.Infrastructure.Agents;
using Praxis.Infrastructure.Composition;
using Praxis.Infrastructure.Persistence;
using Xunit;

namespace Praxis.Infrastructure.Tests.Agents;

public sealed partial class AgentRuntimeTests
{
    [Fact]
    public async Task SeparateConversationsRunTogetherAndShutdownCancelsOnlyRemainingWork()
    {
        using SqliteCatalog catalog = await SeededAsync();
        TaskCompletionSource enteredA = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource enteredB = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseB = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ScriptedBackend backend = new(async (messages, token) =>
        {
            if (messages[^1].Content == "A")
            {
                enteredA.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }

            enteredB.TrySetResult();
            await releaseB.Task.WaitAsync(token);
            return new AgentTurn("B answered", [], 1, 1);
        });
        (AgentRuntime runtime, AgentConversation first) = Runtime(catalog, Local(), backend);
        AgentConversation second = catalog.Agents.Create(first.BackendId, "second");
        AgentConversation third = catalog.Agents.Create(first.BackendId, "third");
        AgentRunCoordinator runs = new(runtime, clock);
        Guid a = runs.Start(first.ConversationId, "A");
        Guid b = runs.Start(second.ConversationId, "B");
        try
        {
            await Task.WhenAll(enteredA.Task, enteredB.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<InvalidOperationException>(() => runs.Start(first.ConversationId, "again"));
            Assert.Throws<InvalidOperationException>(() => runs.Start(third.ConversationId, "C"));
            Assert.Empty(catalog.Agents.ReadMessages(third.ConversationId));
            releaseB.TrySetResult();
            Assert.Equal("B answered", (await Poll(runs, b)).Reply!.Text);
            Assert.Equal(AgentRunStatus.Running, runs.Status(a, 0)!.State);
        }
        finally
        {
            await runs.StopAsync();
        }

        Assert.Equal(AgentRunStatus.Canceled, (await Poll(runs, a)).State);
        Assert.Single(catalog.Agents.ReadMessages(first.ConversationId));
        Assert.Equal("B answered", catalog.Agents.ReadMessages(second.ConversationId)[^1].Content);
    }

    [Fact]
    public async Task CliLimitsHistoryAndToolCallsWithoutSendingSavedMemoriesToRemote()
    {
        using SqliteCatalog catalog = await SeededAsync();
        AgentBackendProfile cli = new("codex", AgentBackendKinds.Codex, null, "gpt-test", null, "/usr/bin/true", true);
        catalog.Agents.SaveBackend(cli);
        AgentConversation conversation = catalog.Agents.Create(cli.BackendId, "cli");
        catalog.Agents.AddMemory(conversation.ConversationId, "private standing preference", AgentMemory.Active);
        catalog.Agents.Append(conversation.ConversationId, AgentMessage.FromUser(new string('u', 25_000)));
        catalog.Agents.Append(conversation.ConversationId, AgentMessage.FromAssistant("Old answer", []));
        SessionBackend session = new(async invoke =>
        {
            Task<string>[] results = Enumerable.Range(0, 14)
                .Select(_ => invoke(Call("compute_evaluate", """{"expression":"2+2"}"""), CancellationToken.None)).ToArray();
            Assert.Equal(2, (await Task.WhenAll(results)).Count(result => result.Contains("tool_budget_exhausted", StringComparison.Ordinal)));
            return new AgentTurn("Done", [], 150, 5);
        }, inspect: transcript =>
        {
            Assert.Equal("Current question", Assert.Single(transcript).Content);
        }, inspectRequest: (system, tools) =>
        {
            Assert.DoesNotContain("private standing preference", system, StringComparison.Ordinal);
            Assert.DoesNotContain(tools, tool => tool.Name is "artifact_create" or "memory_recall");
        });
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(null, session), clock);
        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Current question", (_, _, _) => { }, CancellationToken.None);

        Assert.Equal(2, reply.OmittedMessages);
        Assert.Equal(150, reply.InputTokens);
        Assert.Equal(12, catalog.Agents.ReadToolCalls(conversation.ConversationId, 100).Count);
        Assert.Equal(4, catalog.Agents.ReadMessages(conversation.ConversationId).Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectChatOmitsToolSchemasHistoryAndMemoryFromTheHttpRequest(bool requestTools)
    {
        using SqliteCatalog catalog = await SeededAsync();
        AgentBackendProfile profile = Local();
        catalog.Agents.SaveBackend(profile);
        AgentConversation conversation = catalog.Agents.Create(profile.BackendId, "direct");
        catalog.Agents.AddMemory(conversation.ConversationId, "private standing preference", AgentMemory.Active);
        catalog.Agents.Append(conversation.ConversationId, AgentMessage.FromUser("Previous question"));
        AgentToolCall tool = Call("knowledge_search", """{"query":"previous query"}""");
        catalog.Agents.Append(conversation.ConversationId, AgentMessage.FromAssistant("Searching", [tool]));
        catalog.Agents.Append(conversation.ConversationId, AgentMessage.FromTool(tool.CallId, "tool-only evidence", []));
        catalog.Agents.Append(conversation.ConversationId, AgentMessage.FromAssistant("Earlier answer", []));
        CapturingHandler handler = new("""{"choices":[{"message":{"content":"PING"}}],"usage":{"prompt_tokens":83,"completion_tokens":2}}""");
        using HttpClient http = new(handler);
        var backend = new OpenAiCompatibleAgentBackend(http, new Uri(profile.Endpoint + "/"), profile.Model, null);
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog),
            new FixedFactory(requestTools ? new PlainChatBackend(backend) : backend, null), clock);
        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "Reply with PING only.", (_, _, _) => { },
            CancellationToken.None, useTools: requestTools);

        using JsonDocument payload = JsonDocument.Parse(handler.Body!);
        Assert.False(payload.RootElement.TryGetProperty("tools", out _));
        Assert.DoesNotContain("tool-only evidence", handler.Body!, StringComparison.Ordinal);
        Assert.DoesNotContain("private standing preference", handler.Body!, StringComparison.Ordinal);
        Assert.Contains("Earlier answer", handler.Body!, StringComparison.Ordinal);
        Assert.Equal("PING", reply.Text);
        Assert.Equal(83, reply.InputTokens);
        Assert.Equal(requestTools, reply.ToolsUnavailable);
        Assert.Empty(catalog.Agents.ReadToolCalls(conversation.ConversationId, 20));
        Assert.Equal("PING", catalog.Agents.ReadMessages(conversation.ConversationId)[^1].Content);
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task BrainRecallIsBoundedLocalOnlyAndDoesNotRequireModelTools(bool local, bool useBrain, bool expected)
    {
        using SqliteCatalog catalog = await SeededAsync();
        AgentBackendProfile profile = Local() with
        {
            BackendId = "ollama",
            Endpoint = local ? Local().Endpoint : "https://example.test/v1"
        };
        catalog.Agents.SaveBackend(profile);
        AgentConversation conversation = catalog.Agents.Create(profile.BackendId, "recall");
        CapturingHandler handler = new("""{"choices":[{"message":{"content":"The lease is in the library."}}]}""");
        using HttpClient http = new(handler);
        IAgentBackend backend = new PlainChatBackend(new OpenAiCompatibleAgentBackend(http, new Uri(profile.Endpoint + "/"), profile.Model, null));
        AgentRuntime runtime = AgentComposition.Runtime(catalog, Tools(catalog), new FixedFactory(backend, null), clock);

        AgentReply reply = await runtime.RunAsync(conversation.ConversationId, "What does the lease say?", (_, _, _) => { },
            CancellationToken.None, useTools: useBrain);

        Assert.Equal(expected, handler.Body!.Contains("Monthly rent", StringComparison.Ordinal));
        Assert.Equal(expected, reply.Citations.Any(citation => citation.SourceName == "lease.md"));
        Assert.True(reply.ToolsUnavailable);
        Assert.Empty(reply.ToolCalls);
        Assert.Equal(2, catalog.Agents.ReadMessages(conversation.ConversationId).Count);
    }

    [Theory]
    [InlineData("ollama", "http://127.0.0.1:11434/v1", "none")]
    [InlineData("other-local", "http://127.0.0.1:11434/v1", null)]
    [InlineData("ollama", "https://api.example.com/v1", null)]
    public async Task OnlyTheLoopbackOllamaConnectionRequestsThinkingOff(string id, string endpoint, string? expectedEffort)
    {
        CapturingHandler handler = new("""{"choices":[{"message":{"content":"PING"}}]}""");
        using HttpClient http = new(handler);
        AgentBackendFactory factory = new(http, new NoSecrets(), new AgentRelayCommand("unused", []), Path.GetTempPath());
        IAgentBackend backend = factory.CreateTurnBackend(Local() with { BackendId = id, Endpoint = endpoint })!;

        AgentTurn result = await backend.CompleteAsync("Answer directly.", [AgentMessage.FromUser("Reply with PING only.")], [], CancellationToken.None);

        using JsonDocument payload = JsonDocument.Parse(handler.Body!);
        string? effort = payload.RootElement.TryGetProperty("reasoning_effort", out JsonElement value) ? value.GetString() : null;
        Assert.Equal(expectedEffort, effort);
        Assert.Equal("PING", result.Text);
    }

    private sealed class NoSecrets : ISecretStore
    {
        public string? Read(string account) => throw new InvalidOperationException("This connection must not use a secret.");
    }

    [Fact]
    public void CodexUsageIsProviderReportedAndFailureDetailsStayPrivate()
    {
        var (input, output, failure) = CodexSessionBackend.ReadEvents("""
            missing-telemetry-is-fine
            {"type":"item.completed","item":{"type":"agent_message","text":"approval required"}}
            {"type":"turn.completed","usage":{"input_tokens":222,"cached_input_tokens":111,"output_tokens":19}}
            """);
        Assert.Equal((222, 19, null), (input, output, failure));
        (_, _, failure) = CodexSessionBackend.ReadEvents("""
            {"type":"turn.failed","error":{"message":"tool requires approval: sensitive-source"}}
            """);
        var error = CodexSessionBackend.ClassifyFailure(failure, "");
        Assert.Equal("model_approval_required", error.Code);
        Assert.DoesNotContain("sensitive-source", error.Message, StringComparison.Ordinal);
    }

    private sealed class PlainChatBackend(IAgentBackend inner) : IAgentBackend
    {
        public ValueTask<bool> SupportsToolsAsync(CancellationToken cancellationToken) => ValueTask.FromResult(false);

        public Task<AgentTurn> CompleteAsync(string systemPrompt, IReadOnlyList<AgentMessage> transcript, IReadOnlyList<AgentToolDefinition> tools,
            CancellationToken cancellationToken) => inner.CompleteAsync(systemPrompt, transcript, tools, cancellationToken);
    }
}
