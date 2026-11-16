namespace Praxis.Domain.Agents;

/// <summary>Owner-local record of what automatic recall selected for a provider. No excerpt bodies are stored here.</summary>
public sealed record AgentRecallReceipt(Guid Id, string BackendId, string Outcome, int Considered, int Withheld,
    IReadOnlyList<AgentCitation> Selected, DateTimeOffset CreatedUtc);
