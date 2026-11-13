namespace Praxis.Domain.Policy;

/// <summary>The owner's complete disclosure and tool-permission configuration.</summary>
public sealed record DisclosurePolicy(
    string Mode, IReadOnlyList<DisclosureRule> Rules, IReadOnlyDictionary<string, IReadOnlyList<string>> Fallbacks,
    string ToolPermission, TimeSpan HoldDuration)
{
    public static readonly TimeSpan DefaultHoldDuration = TimeSpan.FromHours(72);

    public static DisclosurePolicy Default { get; } = new(DisclosureModes.StandingGrants, [],
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal), ToolPermissions.ReadOnly, DefaultHoldDuration);

    public IReadOnlyList<string> FallbacksFor(string category) =>
        Fallbacks.TryGetValue(category, out IReadOnlyList<string>? chain) ? chain : FallbackSteps.Default;
}
