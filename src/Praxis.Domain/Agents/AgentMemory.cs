namespace Praxis.Domain.Agents;

/// <summary>An owner preference or standing fact learned in conversation; active only once the owner (or allow_edits) accepts it.</summary>
public sealed record AgentMemory(Guid MemoryId, string Text, string Status, Guid? ConversationId, DateTimeOffset CreatedUtc, string? OwnerStatement = null)
{
    public const string Proposed = "proposed";
    public const string Active = "active";
    public const string Rejected = "rejected";
}
