using Praxis.Domain.Knowledge;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>Entities matching the owner's filter.</summary>
public sealed record EntitiesResponse(IReadOnlyList<EntitySummary> Entities);
