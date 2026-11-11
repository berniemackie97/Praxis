namespace Praxis.Domain.Enrichment;

/// <summary>Owner-governed knowledge about a name that appears in the excerpt.</summary>
public sealed record EntityHint(string Name, string? EntityType, bool Rejected);
