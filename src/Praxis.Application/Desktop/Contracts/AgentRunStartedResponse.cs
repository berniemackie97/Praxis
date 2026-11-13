namespace Praxis.Application.Desktop.Contracts;

/// <summary>The identifier of a background run to poll with agentRun.</summary>
public sealed record AgentRunStartedResponse(Guid RunId);
