namespace Praxis.Domain.Policy;

/// <summary>What intake does, in order, when remote processing is not allowed or not available.</summary>
public static class FallbackSteps
{
    /// <summary>Run the local tier; keep the result only if it passes code verification.</summary>
    public const string LocalIfVerified = "local_if_verified";

    /// <summary>Wait for an owner decision until a deadline, then continue.</summary>
    public const string Hold = "hold";

    /// <summary>Keep the source captured and searchable without derived facts.</summary>
    public const string IndexOnly = "index_only";

    /// <summary>Record the decision; derive nothing.</summary>
    public const string Skip = "skip";

    public static readonly IReadOnlyList<string> All = [LocalIfVerified, Hold, IndexOnly, Skip];

    public static readonly IReadOnlyList<string> Default = [LocalIfVerified, Hold, IndexOnly];

    /// <summary>A chain is valid when it is non-empty, has no repeats, and ends in a terminal step.</summary>
    public static bool IsValidChain(IReadOnlyList<string> chain) =>
        chain.Count is >= 1 and <= 4 && chain.All(step => All.Contains(step, StringComparer.Ordinal)) &&
        chain.Distinct(StringComparer.Ordinal).Count() == chain.Count && chain[^1] is IndexOnly or Skip;
}
