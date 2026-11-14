namespace Praxis.Application.Models;

/// <summary>A model a provider offers, with the reasoning efforts it accepts and its default.</summary>
public sealed record ModelChoice(string Id, string Label, IReadOnlyList<string> Efforts, string? DefaultEffort);
