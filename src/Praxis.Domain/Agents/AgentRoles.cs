namespace Praxis.Domain.Agents;

public static class AgentRoles
{
    public const string User = "user";
    public const string Assistant = "assistant";
    public const string Tool = "tool";

    /// <summary>A compacted summary of earlier turns; transcripts start from the latest one.</summary>
    public const string Summary = "summary";
}
