namespace Praxis.Application.Models;

/// <summary>
/// How Praxis should use this machine's local models: which one stays loaded for every local job,
/// which embedding model serves semantic search, when local drafts are worth it, and whether the
/// optional background enrichment should run.
/// </summary>
public sealed record MachinePlan(
    string Profile, long MemoryBytes, string? LocalModel, string? EmbeddingModel, string DraftMode, int DraftBudgetSeconds, bool Enrichment,
    IReadOnlyList<string> Notes)
{
    public const string Efficient = "efficient";
    public const string Balanced = "balanced";
    public const string Performance = "performance";
}
