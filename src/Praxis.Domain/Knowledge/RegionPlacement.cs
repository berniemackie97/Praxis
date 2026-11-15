namespace Praxis.Domain.Knowledge;

/// <summary>
/// Where a source sits in the owner's own structure: its top region (a folder or repository under a
/// watched or selected root, or the root's loose files) and, when deeper, its child region.
/// </summary>
public sealed record RegionPlacement(string TopKey, string TopLabel, bool Loose, string? ChildKey, string? ChildLabel);
