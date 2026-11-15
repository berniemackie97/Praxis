namespace Praxis.Application.Knowledge;

/// <summary>A canonical entity acting as a group because of current structural evidence.
/// Member counts are direct; the evidence categories can overlap.</summary>
public sealed record GraphConceptRegion(Guid RegionId, string Label, int ContainedEntityCount,
    int OwnerMemberCount, int AcceptedMemberCount, int VerifiedMemberCount);
