namespace Praxis.Domain.Agents;

/// <summary>How many items a tool result left out, and why (for example the disclosure policy for this backend).</summary>
public sealed record WithheldNotice(int Count, string Reason)
{
    public const string DisclosurePolicy = "disclosure_policy";
}
