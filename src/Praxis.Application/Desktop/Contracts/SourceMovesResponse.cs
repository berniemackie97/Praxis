using Praxis.Domain.Sources;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>Observed relocations of a file source.</summary>
public sealed record SourceMovesResponse(IReadOnlyList<SourceMovement> Moves);
