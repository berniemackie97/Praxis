using Praxis.Domain.Agents;

namespace Praxis.Application.Agents;

/// <summary>Durable requests, run receipts and a bounded learning cursor over canonical conversations.</summary>
public interface IAgentRunJournal
{
    void Enqueue(Guid runId, Guid conversationId, string request, bool useTools, string kind);
    bool TryStart(Guid runId);
    AgentRunEvent AppendEvent(Guid runId, string kind, string? tool, string? detail);
    void Finish(Guid runId, string state, AgentReply? reply, string? error);
    AgentRunStatus? Status(Guid runId, int afterSequence);
    void RecoverInterrupted();
    void Suspend(Guid runId);
    IReadOnlyList<AgentWorkRequest> Pending(int limit);
    bool CancelPending(Guid runId);
    bool HasPending(Guid conversationId);
    bool HasActiveDream();
    AgentLearningEpisode? ClaimLearning(int dailyCalls, int dailyTokens, DateTimeOffset? completedBefore = null);
    bool EvidenceCurrent(Guid runId);
    void FinishLearning(Guid runId, string outcome, Guid? skillRevision, int inputTokens, int outputTokens, string? model);
    IReadOnlyList<AgentLearningReceipt> LearningHistory(int limit);
}
