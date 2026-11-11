namespace Praxis.Domain.Knowledge;

/// <summary>
/// An entity and its visible evidence. <see cref="TypeConflict"/> marks an unresolved entity
/// whose mentions disagree about its type; an owner or verified type resolves it.
/// </summary>
public sealed record EntitySummary(
    Guid EntityId, string Name, string? EntityType, string TypeOrigin, string Status,
    int Mentions, int Sources, bool TypeConflict);
