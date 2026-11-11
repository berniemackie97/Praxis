using Praxis.Domain.Knowledge;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>One entity with its aliases, mentions and relations.</summary>
public sealed record EntityResponse(EntityDetail? Entity);
