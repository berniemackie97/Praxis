namespace Praxis.Application.Agents.Memory;

public sealed record LearningToolReceipt(string CallId, string Tool, string EvidenceKind, string Arguments, string ObservedResult)
{
    public static string? KindFor(string tool) => tool switch
    {
        "compute_evaluate" or "data_query" => "deterministic_result",
        "knowledge_open" or "media_find" => "source_observation",
        _ => null
    };
}
