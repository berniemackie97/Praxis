using System.Collections.Concurrent;
using Praxis.Domain.Agents;

namespace Praxis.Application.Agents.Runtime;

/// <summary>
/// Runs agent requests in the background so the single-request desktop pipe stays responsive.
/// One run per conversation at a time; clients poll events and the reply; runs can be canceled.
/// Finished runs are retained briefly for polling and then forgotten (messages stay persisted).
/// </summary>
public sealed class AgentRunCoordinator(AgentRuntime runtime, TimeProvider clock)
{
    private const int MaximumActiveRuns = 2;
    private const int RetainedRuns = 64;
    private static readonly TimeSpan RunDeadline = TimeSpan.FromMinutes(15);
    private readonly Lock admission = new();
    private readonly ConcurrentDictionary<Guid, Run> runs = new();
    private bool stopping;

    public Guid Start(Guid conversationId, string text, bool useTools = true)
    {
        lock (admission)
        {
            if (stopping)
            {
                throw new InvalidOperationException("The worker is stopping.");
            }

            if (HasRunning(conversationId))
            {
                throw new InvalidOperationException("This conversation already has a running request.");
            }

            if (runs.Values.Count(item => item.State == AgentRunStatus.Running) >= MaximumActiveRuns)
            {
                throw new InvalidOperationException("Two chats are already responding. Finish or stop one, then send again.");
            }

            Run run = new(Guid.NewGuid(), conversationId, clock.GetUtcNow());
            runs[run.RunId] = run;
            Prune();
            run.Execution = Task.Run(() => ExecuteAsync(run, text, useTools));
            return run.RunId;
        }
    }

    public async Task StopAsync()
    {
        Run[] active;
        lock (admission)
        {
            stopping = true;
            active = runs.Values.Where(run => run.State == AgentRunStatus.Running).ToArray();
        }

        await Task.WhenAll(active.Select(run => run.Cancellation.CancelAsync()));
        await Task.WhenAll(active.Select(run => run.Execution));
    }

    private async Task ExecuteAsync(Run run, string text, bool useTools)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(run.Cancellation.Token);
        deadline.CancelAfter(DreamCommand.Parse(text) is null ? RunDeadline : TimeSpan.FromMinutes(4));
        try
        {
            AgentReply reply = await runtime.RunAsync(run.ConversationId, text,
                (kind, tool, detail) => run.Add(kind, tool, detail, clock.GetUtcNow()), deadline.Token, useTools: useTools);
            run.Finish(AgentRunStatus.Completed, reply, null, clock.GetUtcNow());
        }
        catch (OperationCanceledException) when (run.Cancellation.IsCancellationRequested)
        {
            run.Finish(AgentRunStatus.Canceled, null, null, clock.GetUtcNow());
        }
        catch (Exception exception)
        {
            string reason = exception switch
            {
                Inference.InferenceException inference => inference.Code,
                OperationCanceledException or TimeoutException => "model_timeout",
                HttpRequestException => "model_unavailable",
                ArgumentException => "invalid_request",
                InvalidOperationException => "backend_configuration_error",
                _ => "agent_internal_error"
            };
            run.Finish(AgentRunStatus.Failed, null, reason, clock.GetUtcNow());
        }
    }

    public bool HasRunning(Guid conversationId) =>
        runs.Values.Any(run => run.ConversationId == conversationId && run.State == AgentRunStatus.Running);

    public bool HasActiveRuns => runs.Values.Any(run => run.State == AgentRunStatus.Running);

    internal T WhileIdle<T>(Guid conversationId, Func<T> change)
    {
        lock (admission)
        {
            if (HasRunning(conversationId))
            {
                throw new InvalidOperationException("Finish or stop this conversation's response first.");
            }

            return change();
        }
    }

    public AgentRunStatus? Status(Guid runId, int afterSequence) =>
        runs.TryGetValue(runId, out Run? run) ? run.Snapshot(afterSequence) : null;

    public bool Cancel(Guid runId)
    {
        if (!runs.TryGetValue(runId, out Run? run) || run.State != AgentRunStatus.Running)
        {
            return false;
        }

        run.Cancellation.Cancel();
        return true;
    }

    private void Prune()
    {
        foreach (Run old in runs.Values.Where(run => run.State != AgentRunStatus.Running).OrderBy(run => run.Created).SkipLast(RetainedRuns))
        {
            runs.TryRemove(old.RunId, out _);
        }
    }

    private sealed class Run(Guid runId, Guid conversationId, DateTimeOffset created)
    {
        private readonly List<AgentRunEvent> events = [];
        private readonly Lock gate = new();

        public Guid RunId { get; } = runId;
        public Guid ConversationId { get; } = conversationId;
        public DateTimeOffset Created { get; } = created;
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Execution { get; set; } = Task.CompletedTask;
        private string state = AgentRunStatus.Running;

        public string State
        {
            get { lock (gate) { return state; } }
        }
        private AgentReply? reply;

        public void Finish(string outcome, AgentReply? answer, string? detail, DateTimeOffset at)
        {
            lock (gate)
            {
                if (outcome != AgentRunStatus.Completed)
                {
                    events.Add(new AgentRunEvent(events.Count + 1, outcome, null, detail, at));
                }

                reply = answer;
                state = outcome;
            }
        }

        public void Add(string kind, string? tool, string? detail, DateTimeOffset at)
        {
            lock (gate)
            {
                events.Add(new AgentRunEvent(events.Count + 1, kind, tool, detail, at));
            }
        }

        public AgentRunStatus Snapshot(int sequence)
        {
            lock (gate)
            {
                return new AgentRunStatus(RunId, ConversationId, state, events.Where(item => item.Sequence > sequence).ToArray(), reply);
            }
        }
    }
}
