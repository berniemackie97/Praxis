namespace Praxis.Domain.Understanding;

/// <summary>
/// A verified intake record of a current, active source revision, with the source it came from and
/// the exact quote that supports it.
/// </summary>
public sealed record StoredRecord(
    Guid SourceId, Guid ItemId, int Revision, string SourceName, string Kind, IReadOnlyDictionary<string, string> Fields,
    string Evidence, int EvidenceOffset, int EvidenceLength, string? SortKey, string Tier);
