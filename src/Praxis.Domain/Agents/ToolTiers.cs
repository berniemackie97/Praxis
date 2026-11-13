namespace Praxis.Domain.Agents;

/// <summary>
/// What a tool can change. Enforced by the runtime from the owner's tool permission, never by the
/// model: <c>read_only</c> allows read; <c>propose_edits</c> adds propose; <c>allow_edits</c> adds
/// write. System actions stay disabled until a separately approved system permission exists.
/// </summary>
public static class ToolTiers
{
    public const string Read = "read";
    public const string Propose = "propose";
    public const string Write = "write";
    public const string System = "system";

    public static readonly IReadOnlyList<string> All = [Read, Propose, Write, System];

    public static bool Permits(string toolPermission, string tier) => tier switch
    {
        Read => true,
        Propose => toolPermission is Policy.ToolPermissions.ProposeEdits or Policy.ToolPermissions.AllowEdits,
        Write => toolPermission == Policy.ToolPermissions.AllowEdits,
        _ => false
    };
}
