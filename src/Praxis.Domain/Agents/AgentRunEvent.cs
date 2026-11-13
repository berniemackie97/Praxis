namespace Praxis.Domain.Agents;

/// <summary>
/// One progress event of a run, in order: <c>thinking</c>, <c>tool_started</c>, <c>tool_finished</c>,
/// <c>checking</c>, <c>repairing</c>, <c>completed</c>, <c>failed</c> or <c>canceled</c>.
/// </summary>
public sealed record AgentRunEvent(int Sequence, string Kind, string? Tool, string? Detail, DateTimeOffset AtUtc);
