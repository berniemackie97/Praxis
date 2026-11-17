namespace Praxis.Domain.Agents;

/// <summary>Sizes of the serialized HTTP JSON request body and forwarded evidence, never the contents themselves.</summary>
public sealed record AgentRequestFootprint(
    int RequestBytes, int MessageBytes, int ToolSchemaBytes, int SystemTextBytes,
    int ToolResultContentBytes, int ToolCallArgumentsBytes, int AdvertisedTools);
