namespace Praxis.Application.Agents.Runtime;

/// <summary>Bounds for one user message: model steps, tool calls, input tokens and transcript size before compaction.</summary>
public sealed record AgentRunLimits(int MaximumSteps = 16, int MaximumToolCalls = 48, int MaximumInputTokens = 600_000, int CompactAfterCharacters = 160_000);
