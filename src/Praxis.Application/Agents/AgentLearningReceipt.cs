namespace Praxis.Application.Agents;

public sealed record AgentLearningReceipt(Guid RunId, Guid ConversationId, string State, Guid? SkillRevisionId,
    int InputTokens, int OutputTokens, string? Model, DateTimeOffset? ReviewedUtc);
