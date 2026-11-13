namespace Praxis.Domain.Understanding;

/// <summary>Where a source stands in intake, shown to the owner and to the agent.</summary>
public static class UnderstandingStatuses
{
    public const string Read = "read";
    public const string Triaged = "triaged";
    public const string Understood = "understood";
    public const string PartiallyVerified = "partially_verified";
    public const string Held = "held";
    public const string IndexOnly = "index_only";
    public const string Skipped = "skipped";
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> All = [Read, Triaged, Understood, PartiallyVerified, Held, IndexOnly, Skipped, Failed];
}
