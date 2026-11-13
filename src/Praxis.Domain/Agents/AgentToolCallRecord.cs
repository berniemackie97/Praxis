namespace Praxis.Domain.Agents;

/// <summary>The audit record of one tool invocation: arguments, outcome, size and evidence returned.</summary>
public sealed record AgentToolCallRecord(
    Guid ConversationId, string CallId, string Tool, string Tier, string ArgumentsJson, string Outcome, int ResultCharacters,
    int Citations, int Withheld, DateTimeOffset StartedUtc, TimeSpan Duration);
