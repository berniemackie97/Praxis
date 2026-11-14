namespace Praxis.Application.Understanding;

/// <summary>One tier's contribution to a document's understanding: what it kept, removed and added, and what it cost.</summary>
public sealed record CascadeStep(string Tier, string? ProviderId, int Kept, int Removed, int Added, int Rejected, int InputTokens, int OutputTokens)
{
    /// <summary>A short line for the owner, such as "local drafted 12" or "strong kept 9, removed 3, added 4".</summary>
    public string Describe(bool first) => first
        ? $"{Tier} drafted {Kept + Added}"
        : $"{Tier} kept {Kept}, removed {Removed}, added {Added}" + (Rejected > 0 ? $" ({Rejected} failed checks)" : string.Empty);
}
