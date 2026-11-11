namespace Praxis.Domain.Knowledge;

public sealed record RelatedEntity(Guid EntityId, string Name, string? EntityType, int SharedSegments);
