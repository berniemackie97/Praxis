namespace Praxis.Domain.Projects;

/// <summary>
/// What intake knows about one code project as a whole: purpose and summary from a model, every
/// path, command and dependency verified in code against the project's own files.
/// </summary>
public sealed record ProjectUnderstanding(
    string ProjectId, string Root, string Name, string Fingerprint, string Status, string? Reason, string? Purpose, string? Summary,
    IReadOnlyList<ProjectComponent> Components, IReadOnlyList<string> EntryPoints, IReadOnlyList<string> BuildCommands,
    IReadOnlyList<string> Dependencies, IReadOnlyDictionary<string, int> Languages, int Members, string? Tier, string? ProviderId,
    long InputTokens, long OutputTokens, DateTimeOffset UpdatedUtc)
{
    public const string Understood = "understood";
    public const string PartiallyVerified = "partially_verified";
    public const string MapOnly = "map_only";
}
