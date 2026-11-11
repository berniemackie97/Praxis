using Praxis.Domain.Models;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>Models installed in a local runtime.</summary>
public sealed record LocalModelsResponse(IReadOnlyList<InstalledModel> Models);
