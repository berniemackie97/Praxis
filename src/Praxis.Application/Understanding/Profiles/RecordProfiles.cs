namespace Praxis.Application.Understanding.Profiles;

/// <summary>The registry of generic record profiles requested in the understanding call.</summary>
public sealed class RecordProfiles(IEnumerable<IRecordProfile> profiles)
{
    private readonly Dictionary<string, IRecordProfile> byKind = profiles.ToDictionary(profile => profile.Kind, StringComparer.Ordinal);

    public static RecordProfiles Default { get; } = new([new EventProfile(), new DeadlineProfile(), new ContactProfile()]);

    public IReadOnlyCollection<string> Kinds => byKind.Keys;

    public IRecordProfile? For(string? kind) => kind is not null && byKind.TryGetValue(kind, out IRecordProfile? profile) ? profile : null;

    /// <summary>The profiles to request for a document, in a stable order.</summary>
    public IReadOnlyList<IRecordProfile> Requested(IEnumerable<string> kinds) =>
        kinds.Distinct(StringComparer.Ordinal).Select(For).OfType<IRecordProfile>().OrderBy(profile => profile.Kind, StringComparer.Ordinal).ToArray();
}
