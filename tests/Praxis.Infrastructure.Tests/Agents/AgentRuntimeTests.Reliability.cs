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
}
