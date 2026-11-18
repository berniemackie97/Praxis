namespace Praxis.Domain.Agents;

/// <summary>One provider completion and its reported usage; request byte counts are unavailable for CLI backends.</summary>
public sealed record AgentTurnUsage(int Turn, int InputTokens, int OutputTokens, int RequestedToolCalls, AgentRequestFootprint? Request,
    int AutomaticRecallBytes = 0, int RecapBytes = 0, int CachedInputTokens = 0);
