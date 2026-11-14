namespace Praxis.Application.Models;

/// <summary>
/// What a model does in Praxis: chat with the owner, or one of the intake tiers (local triage,
/// strong reading, strongest checking). Sub-agent work at chat time uses the local tier.
/// </summary>
public static class ModelRoles
{
    public const string Chat = "chat";

    public static IReadOnlyList<string> All { get; } = [Chat, Domain.Models.ModelTiers.Local, Domain.Models.ModelTiers.Strong, Domain.Models.ModelTiers.Strongest];
}
