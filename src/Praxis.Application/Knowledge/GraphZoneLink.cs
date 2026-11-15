namespace Praxis.Application.Knowledge;

public sealed record GraphZoneLink(string SubjectType, string ObjectType, int ConfirmedCount,
    int SuggestedCount, int ContradictedCount);
