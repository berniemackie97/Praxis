namespace Praxis.Domain.Agents;

/// <summary>Owner-controlled, local-only retrospective learning. A draft never installs itself.</summary>
public sealed record LearningSettings(string Mode = LearningSettings.AfterWork, int DailyCalls = 4, int DailyTokens = 24_000,
    string? Model = null, bool IdleDream = false, int DreamIntervalMinutes = 120)
{
    public const string Key = "agent_learning";
    public const string Off = "off";
    public const string Manual = "manual";
    public const string AfterWork = "after_work";
    public const string Idle = "idle";

    public void Validate()
    {
        if (Mode is not (Off or Manual or AfterWork or Idle) || DailyCalls is < 1 or > 100 || DailyTokens is < 6_000 or > 600_000 ||
            DreamIntervalMinutes is < 30 or > 1_440 || Model is { } model && (model.Length is < 1 or > 100 || model.Any(char.IsWhiteSpace)))
        {
            throw new ArgumentException("The learning mode, model or budget is invalid.");
        }
    }
}
