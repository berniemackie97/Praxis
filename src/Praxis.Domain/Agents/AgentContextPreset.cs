namespace Praxis.Domain.Agents;

/// <summary>Owner-controlled prompt budget. A preset never grants access or changes a model's native token window.</summary>
public static class AgentContextPreset
{
    public const string Compact = "compact";
    public const string Balanced = "balanced";
    public const string Expanded = "expanded";

    public static void Validate(string mode)
    {
        if (mode is not (Compact or Balanced or Expanded))
        {
            throw new ArgumentException("Choose Compact, Balanced, or Expanded context.", nameof(mode));
        }
    }

    public static int HistoryCharacters(string mode) => mode switch
    {
        Compact => 8_000,
        Expanded => 48_000,
        _ => 24_000
    };

    public static int BriefLimit(string mode, bool local) => mode switch
    {
        Compact => 1,
        Expanded => local ? 6 : 4,
        _ => local ? 4 : 3
    };
}
