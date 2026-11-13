namespace Praxis.Domain.Understanding;

/// <summary>
/// Triage's low-trust read of a source: the data category that selects disclosure policy, the
/// kind of document, its language, a one-line gist, and which record kinds to request.
/// </summary>
public sealed record DocumentProfile(string Category, string? DocumentType, string? Language, string? Gist, IReadOnlyList<string> RecordKinds)
{
    public const int MaximumGistLength = 240;
    public const int MaximumTypeLength = 64;
}
