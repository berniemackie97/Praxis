namespace Praxis.Application.Policy;

/// <summary>
/// Decides per span whether the local tier drafts first. In <c>auto</c> mode a draft is asked for
/// only when this Mac's measured pace says it finishes within the budget; before any measurement
/// a short span is tried once so the pace can be learned.
/// </summary>
public sealed class LocalDraftPolicy(ISettingsStore settings, LocalPace pace)
{
    public const string ModeKey = "local_draft_mode";
    public const string BudgetKey = "local_draft_budget_seconds";
    public const int DefaultBudgetSeconds = 20;
    public const int MaximumCharacters = 6_000;
    private const int ProbeCharacters = 2_500;

    public string Mode => settings.Read(ModeKey) is { } mode && LocalDraftModes.IsValid(mode) ? mode : LocalDraftModes.Auto;

    public TimeSpan Budget => TimeSpan.FromSeconds(int.TryParse(settings.Read(BudgetKey), out int seconds) && seconds is >= 5 and <= 600 ? seconds : DefaultBudgetSeconds);

    public bool ShouldDraft(int characters)
    {
        if (characters > MaximumCharacters)
        {
            return false;
        }

        return Mode switch
        {
            LocalDraftModes.Off => false,
            LocalDraftModes.Always => true,
            _ => pace.Estimate(characters) is { } estimate ? estimate <= Budget : characters <= ProbeCharacters
        };
    }
}
