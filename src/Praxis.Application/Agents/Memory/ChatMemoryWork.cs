namespace Praxis.Application.Agents.Memory;

public sealed record ChatMemoryWork(Guid ConversationId, int Through, string? Recap, IReadOnlyList<(bool Owner, string Text)> Dialogue);
