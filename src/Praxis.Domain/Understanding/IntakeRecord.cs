namespace Praxis.Domain.Understanding;

/// <summary>
/// A typed fact a document states — an event, a deadline, a contact — with normalized fields,
/// the exact evidence span it came from, and the tier whose verified output produced it.
/// </summary>
public sealed record IntakeRecord(
    string Kind, IReadOnlyDictionary<string, string> Fields, int EvidenceOffset, int EvidenceLength, string Tier, string? ProviderId, string? SortKey = null)
{
    public const int MaximumFieldLength = 256;
    public const int MaximumRecordsPerDocument = 500;
}
