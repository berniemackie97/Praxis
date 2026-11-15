namespace Praxis.Domain.Agents;

/// <summary>
/// The outcome of one user message: the final answer (or stop reason), tools used, evidence they
/// returned (numbered as cited in the text), items withheld by policy, the answer check, and tokens.
/// </summary>
public sealed record AgentReply(
    Guid ConversationId, string Text, string StopReason, IReadOnlyList<AgentToolCall> ToolCalls, IReadOnlyList<AgentCitation> Citations,
    int WithheldResults, AnswerCheck Check, int InputTokens, int OutputTokens, int OmittedMessages = 0)
{
    public const string Completed = "completed";
    public const string StepLimit = "step_limit";
    public const string TokenBudget = "token_budget";
    public const string BackendFailed = "backend_failed";
}
