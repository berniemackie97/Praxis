using Praxis.Domain.Models;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>The owner's model profile for one role.</summary>
public sealed record ModelProfileResponse(ModelProfile? Profile);
