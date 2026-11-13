namespace Praxis.Domain.Agents;

/// <summary>A run's state, its events after a given sequence, and the reply once finished.</summary>
public sealed record AgentRunStatus(Guid RunId, Guid ConversationId, string State, IReadOnlyList<AgentRunEvent> Events, AgentReply? Reply)
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Canceled = "canceled";
}
