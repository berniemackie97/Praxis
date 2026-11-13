namespace Praxis.Domain.Policy;

/// <summary>Why content would leave the device: background intake or an interactive agent session.</summary>
public static class DisclosurePurposes
{
    public const string Intake = "intake";
    public const string Chat = "chat";

    public static readonly IReadOnlyList<string> All = [Intake, Chat];
}
