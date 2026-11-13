using Praxis.Domain.Policy;

namespace Praxis.Application.Desktop.Contracts;

/// <summary>Intake steps waiting for an owner disclosure decision.</summary>
public sealed record DisclosureHoldsResponse(IReadOnlyList<DisclosureHold> Holds);
