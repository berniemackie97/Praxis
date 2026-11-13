namespace Praxis.Domain.Models;

/// <summary>
/// Owner-configured capability tiers. Any configured provider may fill any tier; the local tier
/// is used only for fast, low-trust triage and verified fallbacks, never as the intake bottleneck.
/// </summary>
public static class ModelTiers
{
    public const string Local = "local";
    public const string Strong = "strong";
    public const string Strongest = "strongest";

    public static readonly IReadOnlyList<string> All = [Local, Strong, Strongest];
}
