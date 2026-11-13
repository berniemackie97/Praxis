namespace Praxis.Domain.Policy;

/// <summary>Owner answers to a disclosure hold.</summary>
public static class HoldDecisions
{
    /// <summary>Send this item once; no standing rule.</summary>
    public const string ApproveOnce = "approve_once";

    /// <summary>Send this item and grant the category to the provider for intake.</summary>
    public const string GrantCategory = "grant_category";

    /// <summary>Keep this item local: continue with the next fallback without disclosure.</summary>
    public const string KeepLocal = "keep_local";

    /// <summary>Derive nothing from this item.</summary>
    public const string Skip = "skip";

    public static readonly IReadOnlyList<string> All = [ApproveOnce, GrantCategory, KeepLocal, Skip];
}
