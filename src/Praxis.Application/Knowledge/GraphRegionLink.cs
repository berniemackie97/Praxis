namespace Praxis.Application.Knowledge;

/// <summary>
/// Evidence connecting two top regions: confirmed relations whose endpoints live in different
/// regions, and entities known from both. Never inferred from proximity or similarity.
/// </summary>
public sealed record GraphRegionLink(string FromRegionId, string ToRegionId, int Relations, int SharedEntities);
