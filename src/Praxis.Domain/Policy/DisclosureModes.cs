namespace Praxis.Domain.Policy;

/// <summary>The default answer to "may this category go to this remote provider?" before rules apply.</summary>
public static class DisclosureModes
{
    public const string LocalOnly = "local_only";
    public const string AskEachTime = "ask_each_time";
    public const string StandingGrants = "standing_grants";
    public const string AllowAll = "allow_all";

    public static readonly IReadOnlyList<string> All = [LocalOnly, AskEachTime, StandingGrants, AllowAll];

    /// <summary>Higher is more permissive; used to tell narrowing from widening changes.</summary>
    public static int Permissiveness(string mode) => mode switch
    {
        LocalOnly => 0,
        AskEachTime => 1,
        StandingGrants => 2,
        AllowAll => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
}
