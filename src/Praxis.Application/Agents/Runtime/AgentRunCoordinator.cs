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
    private const int RetainedRuns = 64;
    private readonly ConcurrentDictionary<Guid, Run> runs = new();

    public Guid Start(Guid conversationId, string text)
    {
        if (runs.Values.Any(run => run.ConversationId == conversationId && run.State == AgentRunStatus.Running))
        {
            throw new InvalidOperationException("This conversation already has a running request.");
        }

        Run run = new(Guid.NewGuid(), conversationId, clock.GetUtcNow());
        runs[run.RunId] = run;
        Prune();
        _ = Task.Run(async () =>
        {
            try
            {
                run.Reply = await runtime.RunAsync(conversationId, text, (kind, tool, detail) => run.Add(kind, tool, detail, clock.GetUtcNow()), run.Cancellation.Token);
                run.State = AgentRunStatus.Completed;
            }
            catch (OperationCanceledException)
            {
                run.Add("canceled", null, null, clock.GetUtcNow());
                run.State = AgentRunStatus.Canceled;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or Inference.InferenceException or HttpRequestException)
            {
                run.Add("failed", null, exception is Inference.InferenceException inference ? inference.Code : exception.Message, clock.GetUtcNow());
                run.State = AgentRunStatus.Failed;
            }
        });
        return run.RunId;
    }

    public AgentRunStatus? Status(Guid runId, int afterSequence) =>
        runs.TryGetValue(runId, out Run? run) ? new AgentRunStatus(run.RunId, run.ConversationId, run.State, run.EventsAfter(afterSequence), run.Reply) : null;

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
        private string state = AgentRunStatus.Running;

        public string State
        {
            get { lock (gate) { return state; } }
            set { lock (gate) { state = value; } }
        }
        public AgentReply? Reply { get; set; }

        public void Add(string kind, string? tool, string? detail, DateTimeOffset at)
        {
            lock (gate)
            {
                events.Add(new AgentRunEvent(events.Count + 1, kind, tool, detail, at));
            }
        }

        public IReadOnlyList<AgentRunEvent> EventsAfter(int sequence)
        {
            lock (gate)
            {
                return events.Where(item => item.Sequence > sequence).ToArray();
            }
        }
    }
}
