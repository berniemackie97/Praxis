namespace Praxis.Domain.Policy;

/// <summary>What the agent may change through tools. Independent of disclosure.</summary>
public static class ToolPermissions
{
    public const string ReadOnly = "read_only";
    public const string ProposeEdits = "propose_edits";
    public const string AllowEdits = "allow_edits";

    public static readonly IReadOnlyList<string> All = [ReadOnly, ProposeEdits, AllowEdits];

    public static int Permissiveness(string permission) => permission switch
    {
        ReadOnly => 0,
        ProposeEdits => 1,
        AllowEdits => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(permission))
    };
}
