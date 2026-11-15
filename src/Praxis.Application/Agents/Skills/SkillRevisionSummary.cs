namespace Praxis.Application.Agents.Skills;

public sealed record SkillRevisionSummary(Guid RevisionId, string Name, string Description, string Digest, Guid? BaseRevisionId,
    Guid? ConversationId, string Origin, string Rationale, string State, DateTimeOffset CreatedUtc);
