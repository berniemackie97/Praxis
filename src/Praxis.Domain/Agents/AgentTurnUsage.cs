namespace Praxis.Domain.Agents;

/// <summary>Sizes of the serialized HTTP request and forwarded evidence, never the contents themselves.</summary>
public sealed record AgentRequestFootprint(
    int RequestBytes, int MessageBytes, int ToolSchemaBytes, int SystemTextBytes,
    int ToolResultContentBytes, int ToolCallArgumentsBytes, int AdvertisedTools);

/// <summary>One provider completion and its reported usage; request byte counts are unavailable for CLI backends.</summary>
public sealed record AgentTurnUsage(int Turn, int InputTokens, int OutputTokens, int RequestedToolCalls, AgentRequestFootprint? Request);
